using HexTeam.Messenger.Core.Models;

namespace HexTeam.Messenger.Core.Protocol;

public sealed class Envelope
{
    public Guid PacketId { get; init; } = Guid.NewGuid();
    public Guid MessageId { get; init; }
    public Guid SessionId { get; init; }
    public Guid OriginNodeId { get; init; }
    public Guid CurrentSenderNodeId { get; set; }
    public Guid TargetNodeId { get; init; }
    public int HopCount { get; set; }
    public int MaxHops { get; init; } = ProtocolConstants.MaxHops;
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public PacketType PacketType { get; init; }
    public byte[] Payload { get; init; } = [];

    public bool IsExpired => HopCount >= MaxHops;

    public Envelope WithNextHop(Guid newSenderNodeId)
    {
        return new Envelope
        {
            PacketId = PacketId,
            MessageId = MessageId,
            SessionId = SessionId,
            OriginNodeId = OriginNodeId,
            CurrentSenderNodeId = newSenderNodeId,
            TargetNodeId = TargetNodeId,
            HopCount = HopCount + 1,
            MaxHops = MaxHops,
            CreatedAtUtc = CreatedAtUtc,
            PacketType = PacketType,
            Payload = Payload
        };
    }
}
