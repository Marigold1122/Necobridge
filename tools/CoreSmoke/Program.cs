using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NekoBridge.Core;

string root = Path.Combine(Path.GetTempPath(), "NekoBridge-CoreSmoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    using var certA = Identity("CoreSmoke-A");
    using var certB = Identity("CoreSmoke-B");
    using var certC = Identity("CoreSmoke-C");
    AppSettings settingsA = Settings("A"), settingsB = Settings("B"), settingsC = Settings("C");
    await using var a = new BridgeNode(settingsA, certA, () => { });
    await using var b = new BridgeNode(settingsB, certB, () => { });
    await using var c = new BridgeNode(settingsC, certC, () => { });
    a.Log += text => Console.WriteLine("[A] " + text);
    b.Log += text => Console.WriteLine("[B] " + text);
    c.Log += text => Console.WriteLine("[C] " + text);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(75));
    var ct = deadline.Token;
    await a.StartAsync(ct); await b.StartAsync(ct); await c.StartAsync(ct);
    b.PairingEnabled = true;
    bool rejected = false;
    try { await a.ConnectAsync(Address(settingsB), "wrong-key", ct); }
    catch (Exception ex) when (ex is System.Security.Authentication.AuthenticationException or IOException) { rejected = true; }
    Require(rejected && settingsA.Peers.Count == 0 && settingsB.Peers.Count == 0, "错误配对密钥必须被拒绝，且不得保存信任");
    Console.WriteLine("PASS wrong pairing key rejected");
    await a.ConnectAsync(Address(settingsB), b.PairingKey, ct); b.PairingEnabled = false;
    c.PairingEnabled = true; await a.ConnectAsync(Address(settingsC), c.PairingKey, ct); c.PairingEnabled = false;
    Require(settingsA.Peers.Count == 2 && settingsB.Peers.Count == 1 && settingsC.Peers.Count == 1, "配对必须双向保存且中继不得传播信任");
    Console.WriteLine("PASS mutual pairing and isolated trust");
    await a.ConnectAsync(Address(settingsB), null, ct); // Replace a live control channel without duplicating it.
    a.Disconnect(b.LocalId);
    await a.ConnectAsync(Address(settingsB), null, ct);
    Require(a.Peers.Single(p => p.Id == b.LocalId).IsConnected, "关闭配对窗口后使用固定证书重连");
    Console.WriteLine("PASS repeated control connect and pinned reconnect without pairing key");
    var atB = new TaskCompletionSource<ControlMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
    var atA = new TaskCompletionSource<ControlMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
    b.MessageReceived += (peer, message) => { if (peer == a.LocalId) atB.TrySetResult(message); };
    a.MessageReceived += (peer, message) => { if (peer == b.LocalId) atA.TrySetResult(message); };
    await a.SendAsync(b.LocalId, new() { Kind = "move", X = 123, Y = 456 }, ct);
    Require((await atB.Task.WaitAsync(ct)).X == 123, "真实控制消息 A→B");
    await b.SendAsync(a.LocalId, new() { Kind = "keepalive", Text = "roundtrip" }, ct);
    Require((await atA.Task.WaitAsync(ct)).Text == "roundtrip", "真实控制消息 B→A");
    Console.WriteLine("PASS control message roundtrip");
    string source = Path.Combine(root, "source", "資料-ねこ");
    Directory.CreateDirectory(Path.Combine(source, "nested", "empty"));
    byte[] binary = RandomNumberGenerator.GetBytes(180321);
    await File.WriteAllBytesAsync(Path.Combine(source, "nested", "猫.bin"), binary, ct);
    await File.WriteAllTextAsync(Path.Combine(source, "你好.txt"), "NekoBridge 回环文件内容\n", ct);
    await File.WriteAllBytesAsync(Path.Combine(source, "zero.txt"), [], ct);
    FileOffer fromB = b.CreateOffer(a.LocalId, [source]);
    FileOffer manifest = await a.GetOfferAsync(b.LocalId, fromB.Id, ct);
    Require(manifest.Entries.Any(e => e.IsDirectory && e.RelativePath.EndsWith("/empty")), "清单保留空目录");
    Require(!System.Text.Json.JsonSerializer.Serialize(manifest).Contains(root, StringComparison.OrdinalIgnoreCase), "清单不得包含发送端源路径");
    string direct = Path.Combine(root, "direct");
    await a.DownloadOfferAsync(b.LocalId, fromB.Id, direct, ct);
    CompareTree(source, Path.Combine(direct, Path.GetFileName(source)));
    Console.WriteLine("PASS nested/unicode/empty/zero-byte direct download");
    var binaryEntry = manifest.Entries.Single(e => e.RelativePath.EndsWith("猫.bin"));
    await using (Stream stream = await a.OpenRemoteFileAsync(b.LocalId, fromB.Id, binaryEntry.Index, ct))
    {
        byte[] received = new byte[binary.Length]; int offset = 0;
        while (offset < received.Length) { int count = stream.Read(received, offset, received.Length - offset); Require(count > 0, "同步流不能提前结束"); offset += count; }
        Require(stream.ReadByte() == -1 && received.SequenceEqual(binary) && !stream.CanWrite && !stream.CanSeek, "同步只读流有界且内容一致");
    }
    Console.WriteLine("PASS bounded synchronous remote stream");
    var retired=b.CreateOffer(a.LocalId,[Path.Combine(source,"nested","猫.bin")]);
    await using(var active=await a.OpenRemoteFileAsync(b.LocalId,retired.Id,0,ct))
    {
        b.RetireOffer(retired.Id);
        using var content=new MemoryStream();await active.CopyToAsync(content,ct);
        Require(content.ToArray().SequenceEqual(binary),"替换剪贴板不可中断已开始的文件读取");
    }
    bool retiredDenied=false;
    try{await a.GetOfferAsync(b.LocalId,retired.Id,ct);}catch(IOException){retiredDenied=true;}
    Require(retiredDenied,"退休offer不得接受新的读取");
    Console.WriteLine("PASS clipboard offer retirement preserves active read and blocks new requests");
    var relay = a.CreateRelayOffer(c.LocalId, b.LocalId, manifest);
    string relayed = Path.Combine(root, "relayed");
    await c.DownloadOfferAsync(a.LocalId, relay.Id, relayed, ct);
    CompareTree(source, Path.Combine(relayed, Path.GetFileName(source)));
    Require(settingsB.Peers.All(p => p.Id != c.LocalId) && settingsC.Peers.All(p => p.Id != b.LocalId), "B和C不得因中继自动配对");
    Console.WriteLine("PASS B→A→C streaming relay without B/C trust");
    bool collision = false;
    try { await c.DownloadOfferAsync(a.LocalId, relay.Id, relayed, ct); } catch (IOException) { collision = true; }
    Require(collision, "已存在同名目标必须报错");
    CompareTree(source, Path.Combine(relayed, Path.GetFileName(source)));
    Require(!Directory.EnumerateDirectories(relayed, ".nekobridge-*").Any(), "失败必须清理暂存目录");
    Console.WriteLine("PASS collision rejected without overwriting and staging cleaned");
    var scoped = a.CreateOffer(b.LocalId, [source]);
    bool wrongRecipient = false;
    try { await c.GetOfferAsync(a.LocalId, scoped.Id, ct); } catch (IOException) { wrongRecipient = true; }
    Require(wrongRecipient, "其他已配对设备也不能读取非本设备的offer");
    a.RevokeOffer(scoped.Id);
    Console.WriteLine("PASS authenticated non-recipient denied");
    await File.AppendAllTextAsync(Path.Combine(source, "你好.txt"), "changed", ct);
    string changedDestination = Path.Combine(root, "changed");
    bool changed = false;
    try { await a.DownloadOfferAsync(b.LocalId, fromB.Id, changedDestination, ct); } catch (IOException) { changed = true; }
    Require(changed && !Directory.EnumerateFileSystemEntries(changedDestination).Any(), "来源变化必须失败并清除部分暂存");
    Console.WriteLine("PASS changed source rejected and staging cleaned");
    b.RevokeOffer(fromB.Id);
    bool revoked = false;
    try { await a.GetOfferAsync(b.LocalId, fromB.Id, ct); } catch (IOException) { revoked = true; }
    Require(revoked, "撤销的文件请求不可读取");
    a.Disconnect(b.LocalId, true);
    Require(settingsA.Peers.All(p => p.Id != b.LocalId), "忘记设备必须删除信任");
    Console.WriteLine("PASS offer and trust revocation");
    Console.WriteLine("CORE SMOKE PASSED");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("CORE SMOKE FAILED: " + ex);
    return 1;
}
finally
{
    try { Directory.Delete(root, true); } catch (Exception ex) { Console.Error.WriteLine("Smoke temporary cleanup failed: " + ex.Message); }
}

static AppSettings Settings(string name)
{
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
    return new() { DeviceName = "CoreSmoke-" + name, Port = port };
}
static string Address(AppSettings settings) => "127.0.0.1:" + settings.Port;
static X509Certificate2 Identity(string name)
{
    using var key = RSA.Create(2048);
    var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2") }, false));
    using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    string password = Guid.NewGuid().ToString("N");
    // Schannel on Windows needs an imported key container, not CreateSelfSigned's ephemeral key.
    return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx, password), password, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
}
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static void CompareTree(string source, string destination)
{
    var sourceDirectories = Directory.GetDirectories(source, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(source, p)).Order().ToArray();
    var targetDirectories = Directory.GetDirectories(destination, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(destination, p)).Order().ToArray();
    Require(sourceDirectories.SequenceEqual(targetDirectories), "目录结构不一致");
    var sourceFiles = Directory.GetFiles(source, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(source, p)).Order().ToArray();
    var targetFiles = Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(destination, p)).Order().ToArray();
    Require(sourceFiles.SequenceEqual(targetFiles), "文件列表不一致");
    foreach (string file in sourceFiles) Require(File.ReadAllBytes(Path.Combine(source, file)).SequenceEqual(File.ReadAllBytes(Path.Combine(destination, file))), "文件字节不一致：" + file);
}
