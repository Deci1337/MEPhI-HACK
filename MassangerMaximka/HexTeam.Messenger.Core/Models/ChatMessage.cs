namespace HexTeam.Messenger.Core.Models;

public sealed record ChatMessage(
    string MessageId,
    string FromNodeId,
    string ToNodeId,
    string Text,
    long TimestampUtc = 0)
{
    public DeliveryStatus Status { get; set; } = DeliveryStatus.Sending;
}

public enum DeliveryStatus
{
    Sending,
    Sent,
    Delivered,
    Failed
}
