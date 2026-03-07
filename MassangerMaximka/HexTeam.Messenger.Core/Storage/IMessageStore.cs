using HexTeam.Messenger.Core.Models;

namespace HexTeam.Messenger.Core.Storage;

public interface IMessageStore
{
    void Add(ChatMessage message);
    IReadOnlyList<ChatMessage> GetBySession(Guid sessionId);
    IReadOnlyList<ChatMessage> GetAll();
    IReadOnlyList<Guid> GetMessageIds(Guid sessionId);
    bool Contains(Guid messageId);
    void UpdateDeliveryState(Guid messageId, MessageDeliveryState state);
    Guid? GetSessionIdForPeer(string nodeId);
}
