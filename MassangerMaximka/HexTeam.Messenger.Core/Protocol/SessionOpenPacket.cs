namespace HexTeam.Messenger.Core.Protocol;

public sealed class SessionOpenPacket
{
    public Guid SessionId { get; init; } = Guid.NewGuid();
    public Guid InitiatorNodeId { get; init; }
    public Guid TargetNodeId { get; init; }
    public DateTimeOffset OpenedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
