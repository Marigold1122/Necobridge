using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace NekoBridge.Core;

public sealed partial class BridgeNode : IAsyncDisposable
{
    private readonly AppSettings settings;
    private readonly X509Certificate2 identity;
    private readonly Action persist;
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<long, Task> tasks = new();
    private readonly Dictionary<Guid, Connection> connections = [];
    private readonly SemaphoreSlim pending = new(24);
    private readonly Queue<DateTime> attempts = new();
    private readonly Dictionary<Guid, CancellationTokenSource> peerLifetimes = [];
    private long taskId;
    private TcpListener? listener;
    private DateTime pairingUntil;
    private string pairingKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
    private DeviceDescriptor localDevice;
    private bool disposed;
    public BridgeNode(AppSettings settings, X509Certificate2 identity, Action persistSettings)
    {
        this.settings = settings; this.identity = identity; persist = persistSettings;
        if (!identity.HasPrivateKey) throw new ArgumentException("身份凭据必须包含私钥。", nameof(identity));
        if (settings.Id == Guid.Empty || settings.Port is < 1 or > 65535) throw new ArgumentException("设备 ID 或端口无效。", nameof(settings));
        localDevice = new(settings.Id, CleanName(settings.DeviceName), 1920, 1080);
    }
    public Guid LocalId => settings.Id;
    public string PairingKey { get { lock (gate) return pairingKey; } }
    public bool PairingEnabled
    {
        get { lock (gate) return DateTime.UtcNow < pairingUntil; }
        set { lock (gate) { if (value) { pairingKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(20)); pairingUntil = DateTime.UtcNow.AddMinutes(5); } else pairingUntil = DateTime.MinValue; } }
    }
    public DeviceDescriptor LocalDevice
    {
        get { lock (gate) return localDevice; }
        set { ValidateDevice(value); if (value.Id != LocalId) throw new ArgumentException("不能修改本机 ID。"); lock (gate) localDevice = value with { Name = CleanName(value.Name) }; }
    }
    public IReadOnlyCollection<PeerSnapshot> Peers
    {
        get { lock (gate) return settings.Peers.Select(p => new PeerSnapshot(p.Id, p.Name, FormatAddress(p.Host, p.Port), connections.ContainsKey(p.Id) ? "已连接" : "未连接", p.Width, p.Height, connections.ContainsKey(p.Id))).ToArray(); }
    }
    public event Action<string>? Log;
    public event Action? PeersChanged;
    public event Action<Guid, ControlMessage>? MessageReceived;
    public event Action<Guid, FileTransferProgress>? TransferProgress;
    private void Report(string text) { try { Log?.Invoke(text); } catch { /* Subscriber exceptions must not break transport cleanup. */ } }
    private void Changed() { try { PeersChanged?.Invoke(); } catch (Exception ex) { Report("设备事件处理失败：" + ex.Message); } }
    private void Track(Func<Task> operation)
    {
        long id = Interlocked.Increment(ref taskId);
        var task = Task.Run(Run); tasks[id] = task;
        _ = task.ContinueWith(_ => tasks.TryRemove(id, out Task? ignored), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        async Task Run() { try { await operation(); } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { } catch (Exception ex) when (lifetime.IsCancellationRequested && (ex is ObjectDisposedException or SocketException)) { } catch (Exception ex) { Report("网络任务失败：" + ex.Message); } }
    }
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (listener != null) return Task.CompletedTask;
            listener = new TcpListener(IPAddress.Any, settings.Port);
            try { listener.Start(32); StartDiscovery(); }
            catch { listener.Stop(); listener = null; throw; }
        }
        Track(AcceptLoopAsync);
        return Task.CompletedTask;
    }
    private async Task AcceptLoopAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            TcpClient client = await listener!.AcceptTcpClientAsync(lifetime.Token);
            if (!pending.Wait(0)) { client.Dispose(); continue; }
            Track(async () => { try { await AcceptAsync(client); } finally { pending.Release(); } });
        }
    }
    private static string Fingerprint(X509Certificate cert) => Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));
    private bool KnownFingerprint(string fp) { lock (gate) return settings.Peers.Any(p => string.Equals(p.Fingerprint, fp, StringComparison.OrdinalIgnoreCase)); }
    private bool Trusted(Guid id, string fp) { lock (gate) return settings.Peers.Any(p => p.Id == id && string.Equals(p.Fingerprint, fp, StringComparison.OrdinalIgnoreCase)); }
    private async Task<SslStream> SecureAsync(TcpClient client, bool server, bool allowPair, string host, CancellationToken ct)
    {
        client.NoDelay = true;
        var ssl = new SslStream(client.GetStream(), false, (_, cert, _, _) =>
        {
            if (cert == null) return false;
            if (KnownFingerprint(Fingerprint(cert))) return true;
            if (!server) return allowPair;
            lock (gate)
            {
                if (DateTime.UtcNow >= pairingUntil) return false;
                while (attempts.TryPeek(out var time) && time < DateTime.UtcNow.AddMinutes(-1)) attempts.Dequeue();
                if (attempts.Count >= 60) return false;
                attempts.Enqueue(DateTime.UtcNow);
                return true; // Provisional TLS only; no application access before reciprocal HMAC proofs.
            }
        });
        try
        {
            if (server) await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = identity, ClientCertificateRequired = true, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, CertificateRevocationCheckMode = X509RevocationMode.NoCheck }, ct).ConfigureAwait(false);
            else await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host, ClientCertificates = new X509CertificateCollection { identity }, LocalCertificateSelectionCallback = (_, _, _, _, _) => identity, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, CertificateRevocationCheckMode = X509RevocationMode.NoCheck }, ct).ConfigureAwait(false);
            if (!ssl.IsMutuallyAuthenticated || ssl.RemoteCertificate == null) throw new AuthenticationException("需要双向 TLS 身份认证。");
            return ssl;
        }
        catch { ssl.Dispose(); throw; }
    }
    private string Proof(string key, string role, WireMessage hello, WireMessage challenge, string clientFp, string serverFp)
    {
        string transcript = $"NekoBridge-v1|{role}|{hello.Device!.Id:D}|{challenge.Device!.Id:D}|{clientFp}|{serverFp}|{hello.Nonce}|{challenge.Nonce}|{hello.Mode}|{hello.OfferId:D}|{hello.Index}";
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(transcript)));
    }
    private static void VerifyProof(string? actual, string expected)
    {
        if (actual == null || actual.Length != 64) throw new AuthenticationException("配对验证失败。");
        byte[] bytes;
        try { bytes = Convert.FromHexString(actual); } catch (FormatException) { throw new AuthenticationException("配对验证失败。"); }
        if (!CryptographicOperations.FixedTimeEquals(bytes, Convert.FromHexString(expected))) throw new AuthenticationException("配对验证失败。");
    }
    private void SavePeer(DeviceDescriptor device, string fp, string host, int port, bool allowNew, string? acceptingPairKey = null)
    {
        lock (gate)
        {
            var existing = settings.Peers.FirstOrDefault(p => p.Id == device.Id);
            if (existing == null && !allowNew) throw new AuthenticationException("配对已撤销。");
            if (existing == null && settings.Peers.Count >= 64) throw new AuthenticationException("配对设备数量已达上限。");
            if (acceptingPairKey != null && (DateTime.UtcNow >= pairingUntil || acceptingPairKey != pairingKey)) throw new AuthenticationException("配对窗口已关闭。");
            if (existing != null && !string.Equals(existing.Fingerprint, fp, StringComparison.OrdinalIgnoreCase)) throw new AuthenticationException("设备身份发生变化，请先移除旧配对。");
            var previousPeers = settings.Peers.Select(p => new PeerSettings { Id = p.Id, Name = p.Name, Host = p.Host, Port = p.Port, Fingerprint = p.Fingerprint, Width = p.Width, Height = p.Height }).ToList();
            var previousLayout = settings.Layout.ToList();
            existing ??= new PeerSettings { Id = device.Id, Fingerprint = fp };
            existing.Name = CleanName(device.Name); existing.Host = host; existing.Port = port; existing.Width = device.Width; existing.Height = device.Height;
            if (!settings.Peers.Contains(existing)) settings.Peers.Add(existing);
            // A versioned shared record must only change together with its Lamport stamp.
            if (settings.LayoutRevision == 0)
            {
                if (!settings.Layout.Contains(LocalId)) settings.Layout.Add(LocalId);
                if (!settings.Layout.Contains(device.Id)) settings.Layout.Add(device.Id);
            }
            try { persist(); }
            catch { settings.Peers = previousPeers; settings.Layout = previousLayout; throw; }
            if (!peerLifetimes.ContainsKey(device.Id)) peerLifetimes[device.Id] = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        }
        Changed();
    }
    private async Task AcceptAsync(TcpClient client)
    {
        SslStream? ssl = null; bool handedOff = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
            ssl = await SecureAsync(client, true, false, "", timeout.Token).ConfigureAwait(false);
            string remoteFp = Fingerprint(ssl.RemoteCertificate!), ownFp = Fingerprint(identity);
            var hello = await Wire.ReadAsync(ssl, timeout.Token).ConfigureAwait(false);
            if (hello.Type != "hello" || hello.Device == null || hello.Mode is not ("control" or "offer" or "file")) throw new InvalidDataException("握手类型无效。");
            ValidateDevice(hello.Device); ValidateNonce(hello.Nonce);
            if (hello.Device.Id == LocalId || hello.Port is < 1 or > 65535) throw new InvalidDataException("握手设备无效。");
            bool trusted = Trusted(hello.Device.Id, remoteFp);
            string key = "";
            if (!trusted)
            {
                lock (gate)
                {
                    if (!hello.Pair || hello.Mode != "control" || DateTime.UtcNow >= pairingUntil || settings.Peers.Any(p => p.Id == hello.Device.Id)) throw new AuthenticationException("设备未配对。");
                    key = pairingKey;
                }
            }
            var challenge = new WireMessage { Type = "challenge", Device = LocalDevice, Port = settings.Port, Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), Pair = !trusted };
            if (!trusted) challenge.Proof = Proof(key, "server", hello, challenge, remoteFp, ownFp);
            await Wire.WriteAsync(ssl, challenge, timeout.Token).ConfigureAwait(false);
            var auth = await Wire.ReadAsync(ssl, timeout.Token).ConfigureAwait(false);
            if (auth.Type != "auth") throw new AuthenticationException("握手顺序无效。");
            if (!trusted)
            {
                VerifyProof(auth.Proof, Proof(key, "client", hello, challenge, remoteFp, ownFp));
                lock (gate) if (DateTime.UtcNow >= pairingUntil || key != pairingKey) throw new AuthenticationException("配对窗口已关闭。");
            }
            else if (!Trusted(hello.Device.Id, remoteFp)) throw new AuthenticationException("配对已撤销。");
            string host = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
            if (hello.Mode == "control") SavePeer(hello.Device, remoteFp, host, hello.Port, !trusted, trusted ? null : key);
            await Wire.WriteAsync(ssl, new WireMessage { Type = "ready" }, timeout.Token).ConfigureAwait(false);
            if (hello.Mode == "control")
            {
                var connection = new Connection(client, ssl, hello.Device.Id, remoteFp, false);
                handedOff = true; await RunControlAsync(connection);
            }
            else
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, PeerToken(hello.Device.Id));
                using var registration = linked.Token.Register(static state => ((TcpClient)state!).Dispose(), client);
                await ServeFileRequestAsync(ssl, hello, linked.Token).ConfigureAwait(false);
            }
        }
        finally { if (!handedOff) { ssl?.Dispose(); client.Dispose(); } }
    }
    public async Task ConnectAsync(string address, string? pairingKey, CancellationToken cancellationToken = default)
    {
        var (host, port) = ParseAddress(address);
        var result = await DialAsync(host, port, pairingKey, "control", Guid.Empty, 0, cancellationToken).ConfigureAwait(false);
        var connection = new Connection(result.Client, result.Stream, result.Device.Id, result.Fingerprint, true);
        Track(() => RunControlAsync(connection));
        try { await connection.Registered.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch { connection.Dispose(); throw; }
    }
    private async Task<(TcpClient Client, SslStream Stream, DeviceDescriptor Device, string Fingerprint)> DialAsync(string host, int port, string? key, string mode, Guid offerId, int index, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var client = new TcpClient(); SslStream? ssl = null;
        try
        {
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            ssl = await SecureAsync(client, false, !string.IsNullOrWhiteSpace(key), host, timeout.Token).ConfigureAwait(false);
            string remoteFp = Fingerprint(ssl.RemoteCertificate!), ownFp = Fingerprint(identity);
            var hello = new WireMessage { Type = "hello", Device = LocalDevice, Port = settings.Port, Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), Pair = !string.IsNullOrWhiteSpace(key), Mode = mode, OfferId = offerId, Index = index };
            await Wire.WriteAsync(ssl, hello, timeout.Token).ConfigureAwait(false);
            var challenge = await Wire.ReadAsync(ssl, timeout.Token).ConfigureAwait(false);
            if (challenge.Type != "challenge" || challenge.Device == null) throw new AuthenticationException("握手响应无效。");
            ValidateDevice(challenge.Device); ValidateNonce(challenge.Nonce);
            if (challenge.Device.Id == LocalId || challenge.Port is < 1 or > 65535) throw new AuthenticationException("远端设备无效。");
            bool trusted = Trusted(challenge.Device.Id, remoteFp);
            string? proof = null;
            if (!trusted)
            {
                if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || !challenge.Pair || mode != "control") throw new AuthenticationException("远端未配对。");
                VerifyProof(challenge.Proof, Proof(key.Trim(), "server", hello, challenge, ownFp, remoteFp));
                proof = Proof(key.Trim(), "client", hello, challenge, ownFp, remoteFp);
            }
            else if (challenge.Pair) throw new AuthenticationException("远端已失去本机配对，请双方移除旧配对后重试。");
            await Wire.WriteAsync(ssl, new WireMessage { Type = "auth", Proof = proof }, timeout.Token).ConfigureAwait(false);
            if ((await Wire.ReadAsync(ssl, timeout.Token)).Type != "ready") throw new AuthenticationException("远端拒绝连接。");
            if (mode == "control") SavePeer(challenge.Device, remoteFp, host, challenge.Port, !trusted);
            return (client, ssl, challenge.Device, remoteFp);
        }
        catch { ssl?.Dispose(); client.Dispose(); throw; }
    }
    private CancellationToken PeerToken(Guid id)
    {
        lock (gate)
        {
            if (!settings.Peers.Any(p => p.Id == id)) throw new AuthenticationException("设备配对已撤销。");
            if (!peerLifetimes.TryGetValue(id, out var cts)) peerLifetimes[id] = cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            return cts.Token;
        }
    }
    private async Task RunControlAsync(Connection connection)
    {
        bool registered = false;
        try
        {
            lock (gate)
            {
                if (!Trusted(connection.Id, connection.Fingerprint)) throw new AuthenticationException("配对已撤销。");
                if (connections.TryGetValue(connection.Id, out var old))
                {
                    bool preferOutgoing = LocalId.CompareTo(connection.Id) < 0;
                    if (old.Outgoing == preferOutgoing && connection.Outgoing != preferOutgoing) { connection.Registered.TrySetResult(); return; }
                    old.Dispose();
                }
                connections[connection.Id] = connection; registered = true;
            }
            connection.Registered.TrySetResult(); Changed();
            using var ct = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, connection.Stop.Token, PeerToken(connection.Id));
            Task heartbeat = HeartbeatAsync(connection, ct.Token);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct.Token); idle.CancelAfter(TimeSpan.FromSeconds(35));
                    var frame = await Wire.ReadAsync(connection.Stream, idle.Token);
                    if (!Trusted(connection.Id, connection.Fingerprint)) throw new AuthenticationException("配对已撤销。");
                    if (frame.Type == "ping") await connection.WriteAsync(new WireMessage { Type = "pong" }, ct.Token);
                    else if (frame.Type == "pong") { }
                    else if (frame.Type == "message" && frame.Message != null)
                    {
                        ValidateMessage(frame.Message);
                        try { MessageReceived?.Invoke(connection.Id, frame.Message); } catch (Exception ex) { Report("消息处理失败：" + ex.Message); }
                    }
                    else throw new InvalidDataException("控制消息无效。");
                }
            }
            finally { ct.Cancel(); try { await heartbeat; } catch (OperationCanceledException) { } }
        }
        catch (Exception ex)
        {
            connection.Registered.TrySetException(ex);
            if (!lifetime.IsCancellationRequested) Report("连接结束：" + ex.Message);
        }
        finally
        {
            connection.Dispose();
            lock (gate) if (registered && connections.TryGetValue(connection.Id, out var current) && ReferenceEquals(current, connection)) connections.Remove(connection.Id);
            Changed();
        }
    }
    private async Task HeartbeatAsync(Connection connection, CancellationToken ct)
    {
        try { using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10)); while (await timer.WaitForNextTickAsync(ct)) await connection.WriteAsync(new WireMessage { Type = "ping" }, ct).ConfigureAwait(false); }
        catch { connection.Dispose(); throw; }
    }
    public async Task SendAsync(Guid peerId, ControlMessage message, CancellationToken cancellationToken = default)
    {
        ValidateMessage(message);
        Connection connection; lock (gate) connection = connections.GetValueOrDefault(peerId) ?? throw new IOException("设备未连接。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token, connection.Stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await connection.WriteAsync(new WireMessage { Type = "message", Message = message }, timeout.Token).ConfigureAwait(false); }
        catch { connection.Dispose(); throw; }
    }
    public void Disconnect(Guid peerId, bool forget = false)
    {
        lock (gate)
        {
            if (connections.Remove(peerId, out var connection)) connection.Dispose();
            if (peerLifetimes.Remove(peerId, out var cts)) { cts.Cancel(); cts.Dispose(); }
            if (forget) { settings.Peers.RemoveAll(p => p.Id == peerId); if (settings.LayoutRevision == 0) settings.Layout.RemoveAll(id => id == peerId); persist(); }
            foreach (var id in offers.Where(p => p.Value.Recipient == peerId || p.Value.SourcePeer == peerId).Select(p => p.Key).ToArray()) offers.Remove(id);
        }
        Changed();
    }
    private static void ValidateMessage(ControlMessage message)
    {
        if (message.Kind is not ("enter" or "leave" or "move" or "button" or "wheel" or "key" or "keepalive" or "yield" or "clipboard" or "capture" or "dragoffer" or "dragready" or "capture2" or "captured2" or "prepare2" or "commit2" or "started2" or "release2" or "released2" or "abort2" or "dragcancel" or "manualoffer" or "manualdone" or "layout" or "layout2")) throw new InvalidDataException("未知控制事件。");
        if (message.Text?.Length > 512000 || message.Payload?.Length > 512000) throw new InvalidDataException("控制消息过大。");
    }
    private static void ValidateDevice(DeviceDescriptor d)
    {
        if (d.Id == Guid.Empty || string.IsNullOrWhiteSpace(d.Name) || d.Name.Length > 80 || d.Name.Any(char.IsControl) || d.Width is < 1 or > 65536 || d.Height is < 1 or > 65536) throw new InvalidDataException("设备信息无效。");
    }
    private static string CleanName(string name)
    {
        string clean = new((name ?? "").Where(c => !char.IsControl(c)).Take(80).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "NekoBridge" : clean.Trim();
    }
    private static void ValidateNonce(string? nonce) { if (nonce == null || nonce.Length != 64 || !nonce.All(Uri.IsHexDigit)) throw new AuthenticationException("随机挑战无效。"); }
    private static string FormatAddress(string host, int port) => host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";
    private (string Host, int Port) ParseAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Length > 300 || !Uri.TryCreate("tcp://" + address.Trim(), UriKind.Absolute, out var uri) || uri.Host.Length == 0 || uri.AbsolutePath is not ("" or "/") || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) throw new ArgumentException("请输入主机名/IP 或 主机:端口。");
        int port = uri.Port == -1 ? settings.Port : uri.Port;
        if (port is < 1 or > 65535) throw new ArgumentException("端口无效。");
        return (uri.Host.Trim('[', ']'), port);
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed) return; disposed = true; lifetime.Cancel(); listener?.Stop(); discoverySocket?.Dispose();
            foreach (var connection in connections.Values) connection.Dispose();
            connections.Clear(); foreach (var cts in peerLifetimes.Values) { cts.Cancel(); cts.Dispose(); } peerLifetimes.Clear(); offers.Clear();
        }
        while (!tasks.IsEmpty) await Task.WhenAll(tasks.Values.ToArray()).ConfigureAwait(false);
        lifetime.Dispose();
    }
    private sealed class Connection(TcpClient client, SslStream stream, Guid id, string fingerprint, bool outgoing) : IDisposable
    {
        internal Guid Id => id;
        internal string Fingerprint => fingerprint;
        internal bool Outgoing => outgoing;
        internal SslStream Stream => stream;
        internal CancellationTokenSource Stop { get; } = new();
        internal TaskCompletionSource Registered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim write = new(1);
        private int closed;
        internal async Task WriteAsync(WireMessage frame, CancellationToken ct) { await write.WaitAsync(ct); try { await Wire.WriteAsync(stream, frame, ct).ConfigureAwait(false); } finally { write.Release(); } }
        public void Dispose() { if (Interlocked.Exchange(ref closed, 1) != 0) return; Stop.Cancel(); stream.Dispose(); client.Dispose(); }
    }
}
