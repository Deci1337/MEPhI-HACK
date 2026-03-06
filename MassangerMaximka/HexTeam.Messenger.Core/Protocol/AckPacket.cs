namespace HexTeam.Messenger.Core.Protocol;

public sealed class AckPacket
{
    public Guid AckedPacketId { get; init; }
    public Guid AckedMessageId { get; init; }
    public AckStatus Status { get; init; }
}

public enum AckStatus : byte
{
    Delivered = 1,
    Relayed = 2,
    Failed = 3
}
