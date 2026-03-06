using System.Security.Cryptography;

namespace HexTeam.Messenger.Core.Security;

/// <summary>
/// AES-256-GCM based stream encryptor for peer-to-peer traffic.
/// Each frame: [4-byte len][12-byte nonce][16-byte tag][ciphertext]
/// Shared key is derived via ECDH during handshake.
/// </summary>
public sealed class TrafficEncryptor
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public TrafficEncryptor(byte[] sharedKey)
    {
        if (sharedKey.Length < 32)
            throw new ArgumentException("Key must be at least 32 bytes");
        _key = sharedKey[..32];
    }

    public byte[] Encrypt(byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var result = new byte[NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, result, NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, result, NonceSize + TagSize, ciphertext.Length);
        return result;
    }

    public byte[] Decrypt(byte[] frame)
    {
        if (frame.Length < NonceSize + TagSize)
            throw new CryptographicException("Frame too short");

        var nonce = frame.AsSpan(0, NonceSize);
        var tag = frame.AsSpan(NonceSize, TagSize);
        var ciphertext = frame.AsSpan(NonceSize + TagSize);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    public static async Task WriteEncryptedFrameAsync(Stream stream, byte[] plaintext, TrafficEncryptor encryptor, CancellationToken ct = default)
    {
        var encrypted = encryptor.Encrypt(plaintext);
        var lenBytes = BitConverter.GetBytes(encrypted.Length);
        await stream.WriteAsync(lenBytes, ct);
        await stream.WriteAsync(encrypted, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<byte[]?> ReadEncryptedFrameAsync(Stream stream, TrafficEncryptor encryptor, CancellationToken ct = default)
    {
        var lenBuf = new byte[4];
        if (await ReadExactAsync(stream, lenBuf, ct) < 4) return null;

        var length = BitConverter.ToInt32(lenBuf);
        if (length <= 0 || length > 10 * 1024 * 1024) return null;

        var frame = new byte[length];
        if (await ReadExactAsync(stream, frame, ct) < length) return null;

        return encryptor.Decrypt(frame);
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct);
            if (read == 0) return totalRead;
            totalRead += read;
        }
        return totalRead;
    }
}
