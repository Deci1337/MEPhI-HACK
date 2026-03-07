using HexTeam.Messenger.Core.Protocol;

namespace HexTeam.Messenger.Core.Models;

public static class ProtocolConstants
{
    public const int MaxHops = 5;
    public const int MaxRetryCount = 3;
    public static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan SyncTimeout = TimeSpan.FromSeconds(30);
    public const int FileChunkSizeBytes = 64 * 1024;
    public const string HashAlgorithm = "SHA256";
    public const int SeenPacketCacheDuration = 300;
    public const int RelayQueueCapacity = 256;
    public const int SeenPacketPruneIntervalSeconds = 60;

    private static readonly HashSet<PacketType> _ackRequired = new()
    {
        PacketType.ChatEnvelope,
        PacketType.FileChunk,
        PacketType.FileResumeRequest
    };

    public static bool RequiresAck(PacketType type) => _ackRequired.Contains(type);
}
