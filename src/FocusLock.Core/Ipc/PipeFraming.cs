using System.Text;
using System.Text.Json;

namespace FocusLock.Core.Ipc;

/// <summary>
/// Length-prefixed JSON framing for Named Pipe messages.
/// Wire format: 4-byte little-endian int32 length, then UTF-8 JSON payload.
/// </summary>
public static class PipeFraming
{
    public static async Task WriteMessageAsync(Stream stream, PipeMessage message, CancellationToken ct = default)
    {
        byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        byte[] lengthPrefix = BitConverter.GetBytes(json.Length);
        await stream.WriteAsync(lengthPrefix, ct);
        await stream.WriteAsync(json, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<PipeMessage?> ReadMessageAsync(Stream stream, CancellationToken ct = default)
    {
        byte[] lengthBuf = new byte[4];
        int read = await ReadExactAsync(stream, lengthBuf, ct);
        if (read == 0) return null;

        int length = BitConverter.ToInt32(lengthBuf, 0);
        if (length <= 0 || length > 1_048_576) return null;

        byte[] payload = new byte[length];
        await ReadExactAsync(stream, payload, ct);
        return JsonSerializer.Deserialize<PipeMessage>(payload);
    }

    public static PipeMessage BuildRequest(string type, object? payload = null)
        => new(type, JsonSerializer.Serialize(payload ?? new { }));

    public static T? ParsePayload<T>(PipeMessage message)
        => JsonSerializer.Deserialize<T>(message.Payload);

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(totalRead), ct);
            if (n == 0) return totalRead;
            totalRead += n;
        }
        return totalRead;
    }
}
