using HexTeam.Messenger.Core.Models;
using System.Collections.Concurrent;

namespace HexTeam.Messenger.Core.Storage;

public sealed class InMemoryMessageStore : IMessageStore
{
    private readonly ConcurrentDictionary<Guid, ChatMessage> _messages = new();

    public void Add(ChatMessage message)
    {
        _messages.TryAdd(message.MessageId, message);
    }

    public IReadOnlyList<ChatMessage> GetBySession(Guid sessionId) =>
        _messages.Values
            .Where(m => m.SessionId == sessionId)
            .OrderBy(m => m.SentAtUtc)
            .ToList();

    public IReadOnlyList<ChatMessage> GetAll() => _messages.Values.ToList();

    public IReadOnlyList<Guid> GetMessageIds(Guid sessionId) =>
        _messages.Values
            .Where(m => m.SessionId == sessionId)
            .Select(m => m.MessageId)
            .ToList();

    public bool Contains(Guid messageId) => _messages.ContainsKey(messageId);

    public void UpdateDeliveryState(Guid messageId, MessageDeliveryState state)
    {
        if (_messages.TryGetValue(messageId, out var msg))
            msg.DeliveryState = state;
    }
}
