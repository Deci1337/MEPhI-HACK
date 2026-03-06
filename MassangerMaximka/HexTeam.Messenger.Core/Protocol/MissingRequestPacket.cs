namespace HexTeam.Messenger.Core.Protocol;

public sealed class MissingRequestPacket
{
    public Guid SessionId { get; init; }
    public List<Guid> MissingMessageIds { get; init; } = [];
}
