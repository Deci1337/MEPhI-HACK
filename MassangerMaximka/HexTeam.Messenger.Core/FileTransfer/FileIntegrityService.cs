using System.Security.Cryptography;

namespace HexTeam.Messenger.Core.FileTransfer;

public static class FileIntegrityService
{
    public static async Task<string> ComputeFileHashAsync(string filePath, CancellationToken ct = default)
    {
        using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string ComputeChunkHash(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static bool VerifyChunkHash(byte[] data, string expectedHash) =>
        ComputeChunkHash(data) == expectedHash;
}
