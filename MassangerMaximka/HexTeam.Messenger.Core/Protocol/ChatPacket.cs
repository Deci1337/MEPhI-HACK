namespace HexTeam.Messenger.Core.Protocol;

public sealed class ChatPacket
{
    public Guid MessageId { get; init; } = Guid.NewGuid();
    public string Text { get; init; } = string.Empty;
    public DateTimeOffset SentAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
