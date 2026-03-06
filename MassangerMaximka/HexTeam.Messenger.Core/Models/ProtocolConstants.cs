namespace HexTeam.Messenger.Core.Models;

public static class ProtocolConstants
{
    public const int MaxHops = 5;
    public const int MaxRetryCount = 3;
    public static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(5);
    public const int FileChunkSizeBytes = 64 * 1024; // 64 KB
    public const string HashAlgorithm = "SHA256";
    public const int SeenPacketCacheDuration = 300; // seconds
    public const int RelayQueueCapacity = 256;
}
