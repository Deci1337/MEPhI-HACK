using System.Text.Json;

namespace HexTeam.Messenger.Core.Transport;

public static class EnvelopeSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static byte[] Serialize(TransportEnvelope envelope)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(envelope, Options);
        var length = BitConverter.GetBytes(json.Length);
        var result = new byte[4 + json.Length];
        Buffer.BlockCopy(length, 0, result, 0, 4);
        Buffer.BlockCopy(json, 0, result, 4, json.Length);
        return result;
    }

    public static TransportEnvelope? Deserialize(byte[] data)
    {
        return JsonSerializer.Deserialize<TransportEnvelope>(data, Options);
    }

    public static async Task<TransportEnvelope?> ReadFromStreamAsync(Stream stream, CancellationToken ct = default)
    {
        var lengthBuf = new byte[4];
        var read = await ReadExactAsync(stream, lengthBuf, 0, 4, ct);
        if (read < 4) return null;

        var length = BitConverter.ToInt32(lengthBuf, 0);
        if (length <= 0 || length > 10 * 1024 * 1024) return null;

        var jsonBuf = new byte[length];
        read = await ReadExactAsync(stream, jsonBuf, 0, length, ct);
        if (read < length) return null;

        return JsonSerializer.Deserialize<TransportEnvelope>(jsonBuf, Options);
    }

    public static async Task WriteToStreamAsync(Stream stream, TransportEnvelope envelope, CancellationToken ct = default)
    {
        var data = Serialize(envelope);
        await stream.WriteAsync(data, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset + totalRead, count - totalRead), ct);
            if (read == 0) break;
            totalRead += read;
        }
        return totalRead;
    }
}
