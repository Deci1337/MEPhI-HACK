using HexTeam.Messenger.Core.Models;
using HexTeam.Messenger.Core.Storage;

namespace HexTeam.Messenger.Tests;

public class MessageStoreTests
{
    private static readonly Guid NodeA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");

    [Fact]
    public void GetSessionIdForPeer_returns_session_for_known_sender()
    {
        var store = new InMemoryMessageStore();
        var sessionId = Guid.NewGuid();

        store.Add(new ChatMessage
        {
            MessageId = Guid.NewGuid(),
            SessionId = sessionId,
            SenderNodeId = NodeA
        });

        var result = store.GetSessionIdForPeer(NodeA.ToString());

        Assert.Equal(sessionId, result);
    }

    [Fact]
    public void GetSessionIdForPeer_returns_null_for_unknown_peer()
    {
        var store = new InMemoryMessageStore();

        var result = store.GetSessionIdForPeer(Guid.NewGuid().ToString());

        Assert.Null(result);
    }

    [Fact]
    public void GetSessionIdForPeer_returns_null_for_empty_store()
    {
        var store = new InMemoryMessageStore();

        Assert.Null(store.GetSessionIdForPeer(NodeA.ToString()));
    }
}
