namespace HexTeam.Messenger.Core.Transport;

public sealed record TransportEnvelope
{
    public required string PacketId { get; init; }
    public required TransportPacketType Type { get; init; }
    public required string SourceNodeId { get; init; }
    public required string DestinationNodeId { get; init; }
    public int HopCount { get; init; }
    public int MaxHops { get; init; } = 5;
    public long TimestampUtc { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public byte[] Payload { get; init; } = [];

    public static string NewPacketId() => Guid.NewGuid().ToString("N")[..16];
}

public enum TransportPacketType : byte
{
    Hello = 1,
    Chat = 2,
    Ack = 3,
    FileHeader = 10,
    FileChunk = 11,
    FileChunkAck = 12,
    FileComplete = 13,
    FileResumeRequest = 14,
    VoiceFrame = 20,
    Relay = 30,
    Discovery = 40,
    Disconnect = 50,
    Inventory = 60,
    MissingRequest = 61,
    KeyExchange = 100
}
