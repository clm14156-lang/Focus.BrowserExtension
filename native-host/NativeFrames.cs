using System.Buffers.Binary;
using System.Text.Json;

namespace FocusApp.BrowserBridge;

public static class NativeFrames
{
    public const int MaximumBytes = 256 * 1024;

    public static async Task<JsonDocument?> ReadAsync(Stream input, CancellationToken ct)
    {
        var header = new byte[4];
        var first = await input.ReadAsync(header.AsMemory(0, 1), ct);
        if (first == 0) return null;
        await input.ReadExactlyAsync(header.AsMemory(1), ct);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size <= 0 || size > MaximumBytes) throw new BridgeException("PAYLOAD_TOO_LARGE", "Native 帧长度无效", false);
        var body = new byte[size];
        await input.ReadExactlyAsync(body, ct);
        return JsonDocument.Parse(body);
    }

    // Caller owns the output gate. No Console.WriteLine may target stdout.
    public static async Task WriteAsync(Stream output, object value, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, BridgeJson.Options);
        if (body.Length > MaximumBytes) throw new BridgeException("PAYLOAD_TOO_LARGE", "精简快照超过 256 KiB");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await output.WriteAsync(header, ct);
        await output.WriteAsync(body, ct);
        await output.FlushAsync(ct);
    }
}
