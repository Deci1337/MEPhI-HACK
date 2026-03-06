using HexTeam.Messenger.Core.Storage;

namespace HexTeam.Messenger.Tests;

public class SeenPacketStoreTests
{
    [Fact]
    public void First_packet_marked_seen_returns_true()
    {
        var store = new InMemorySeenPacketStore();
        var id = Guid.NewGuid();

        Assert.True(store.TryMarkSeen(id));
    }

    [Fact]
    public void Second_mark_for_same_id_returns_false()
    {
        var store = new InMemorySeenPacketStore();
        var id = Guid.NewGuid();

        store.TryMarkSeen(id);
        Assert.False(store.TryMarkSeen(id));
    }

    [Fact]
    public void HasSeen_returns_true_after_marking()
    {
        var store = new InMemorySeenPacketStore();
        var id = Guid.NewGuid();

        store.TryMarkSeen(id);

        Assert.True(store.HasSeen(id));
    }

    [Fact]
    public void HasSeen_returns_false_for_unknown_packet()
    {
        var store = new InMemorySeenPacketStore();

        Assert.False(store.HasSeen(Guid.NewGuid()));
    }
}
