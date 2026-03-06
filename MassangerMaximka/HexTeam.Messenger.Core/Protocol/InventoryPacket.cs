namespace HexTeam.Messenger.Core.Protocol;

public sealed class InventoryPacket
{
    public Guid SessionId { get; init; }
    public List<Guid> MessageIds { get; init; } = [];
    public DateTimeOffset SinceUtc { get; init; }
}
