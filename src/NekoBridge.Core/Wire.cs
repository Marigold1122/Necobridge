using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;

namespace NekoBridge.Core;

internal sealed class WireMessage
{
    public string Type { get; set; } = "";
    public DeviceDescriptor? Device { get; set; }
    public int Port { get; set; }
    public string? Nonce { get; set; }
    public string? Proof { get; set; }
    public bool Pair { get; set; }
    public string Mode { get; set; } = "control";
    public Guid OfferId { get; set; }
    public int Index { get; set; }
    public FileOffer? Offer { get; set; }
    public long Length { get; set; }
    public ControlMessage? Message { get; set; }
}
internal static class Wire
{
    internal const int MaxFrame = 2 * 1024 * 1024;
    // Omitted zero/null fields deserialize to the same defaults on older peers.
    // Keep case/depth and framing unchanged; pairing proofs use parsed values.
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = false, MaxDepth = 24, DefaultIgnoreCondition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault };
    internal static async Task WriteAsync(Stream stream, WireMessage message, CancellationToken ct)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (bytes.Length > MaxFrame) throw new InvalidDataException("消息超过 2 MiB 限制。");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
    internal static async Task<WireMessage> ReadAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is <= 0 or > MaxFrame) throw new InvalidDataException("非法消息长度。");
        // Reuse ordinary control-frame buffers. Large metadata frames stay
        // unpooled so an occasional 2 MiB message cannot become a permanent cache.
        bool pooled=length<=65536;
        byte[] bytes=pooled?ArrayPool<byte>.Shared.Rent(length):new byte[length];
        try
        {
            await stream.ReadExactlyAsync(bytes.AsMemory(0,length), ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<WireMessage>(bytes.AsSpan(0,length), Json) ?? throw new InvalidDataException("空消息。");
        }
        finally
        {
            if(pooled)
            {
                // Frames may contain clipboard data and pairing proofs.
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes.AsSpan(0,length));
                ArrayPool<byte>.Shared.Return(bytes);
            }
        }
    }
}
