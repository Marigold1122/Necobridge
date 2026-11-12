using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace NekoBridge.Core;

public sealed partial class BridgeNode
{
    private UdpClient? discoverySocket;
    private sealed record DiscoveryPacket(string Protocol, string Type, Guid Nonce, Guid Id, string Name, int Port);
    private void StartDiscovery()
    {
        discoverySocket = new UdpClient(new IPEndPoint(IPAddress.Any, settings.Port));
        Track(DiscoveryLoopAsync);
    }
    private async Task DiscoveryLoopAsync()
    {
        DateTime window = DateTime.UtcNow; int replies = 0;
        while (!lifetime.IsCancellationRequested)
        {
            var received = await discoverySocket!.ReceiveAsync(lifetime.Token);
            if (received.Buffer.Length > 1024) continue;
            DiscoveryPacket? packet;
            try { packet = JsonSerializer.Deserialize<DiscoveryPacket>(received.Buffer, Wire.Json); } catch (JsonException) { continue; }
            if (packet is not { Protocol: "NekoBridge/1", Type: "find" } || packet.Nonce == Guid.Empty) continue;
            if (DateTime.UtcNow - window > TimeSpan.FromSeconds(1)) { window = DateTime.UtcNow; replies = 0; }
            if (++replies > 12) continue;
            var device = LocalDevice;
            byte[] reply = JsonSerializer.SerializeToUtf8Bytes(new DiscoveryPacket("NekoBridge/1", "found", packet.Nonce, device.Id, device.Name, settings.Port), Wire.Json);
            await discoverySocket.SendAsync(reply, received.RemoteEndPoint, lifetime.Token);
        }
    }
    public async Task<IReadOnlyList<DiscoveredPeer>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        using var client = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        client.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        Guid nonce = Guid.NewGuid();
        byte[] request = JsonSerializer.SerializeToUtf8Bytes(new DiscoveryPacket("NekoBridge/1", "find", nonce, Guid.Empty, "", 0), Wire.Json);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(4));
        await client.SendAsync(request, new IPEndPoint(IPAddress.Broadcast, settings.Port), timeout.Token);
        Dictionary<Guid, DiscoveredPeer> found = []; int packets = 0;
        try
        {
            while (found.Count < 64 && packets++ < 256)
            {
                var response = await client.ReceiveAsync(timeout.Token);
                if (response.Buffer.Length > 1024) continue;
                DiscoveryPacket? packet;
                try { packet = JsonSerializer.Deserialize<DiscoveryPacket>(response.Buffer, Wire.Json); } catch (JsonException) { continue; }
                if (packet is not { Protocol: "NekoBridge/1", Type: "found" } || packet.Nonce != nonce || packet.Id == Guid.Empty || packet.Id == LocalId || packet.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(packet.Name) || packet.Name.Length > 80 || packet.Name.Any(char.IsControl)) continue;
                lock (gate) if (settings.Peers.Any(p => p.Id == packet.Id)) continue;
                found.TryAdd(packet.Id, new(packet.Id, packet.Name, response.RemoteEndPoint.Address.ToString(), packet.Port));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested) { }
        return found.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
