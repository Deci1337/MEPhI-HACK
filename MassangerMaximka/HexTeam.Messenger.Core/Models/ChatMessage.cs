namespace HexTeam.Messenger.Core.Models;

public sealed class ChatMessage
{
    public Guid MessageId { get; init; } = Guid.NewGuid();
    public Guid SessionId { get; init; }
    public Guid SenderNodeId { get; init; }
    public string SenderDisplayName { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public DateTimeOffset SentAtUtc { get; init; }
    public DateTimeOffset ReceivedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public MessageDeliveryState DeliveryState { get; set; } = MessageDeliveryState.Pending;
    public bool IsRelayed { get; init; }
}

public enum MessageDeliveryState
{
    Pending,
    Sent,
    Relayed,
    Delivered,
    Failed
}
