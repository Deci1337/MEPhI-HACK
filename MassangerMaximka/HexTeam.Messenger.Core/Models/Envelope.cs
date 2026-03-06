namespace HexTeam.Messenger.Core.Models;

public sealed record Envelope
{
    public required string PacketId { get; init; }
    public required PacketType Type { get; init; }
    public required string SourceNodeId { get; init; }
    public required string DestinationNodeId { get; init; }
    public int HopCount { get; init; }
    public int MaxHops { get; init; } = 5;
    public long TimestampUtc { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public byte[] Payload { get; init; } = [];

    public static string NewPacketId() => Guid.NewGuid().ToString("N")[..16];
}
