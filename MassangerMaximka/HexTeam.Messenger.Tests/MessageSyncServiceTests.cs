using HexTeam.Messenger.Core.Models;
using HexTeam.Messenger.Core.Protocol;
using HexTeam.Messenger.Core.Services;
using HexTeam.Messenger.Core.Storage;

namespace HexTeam.Messenger.Tests;

public class MessageSyncServiceTests
{
    private static Guid NodeA => Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
    private static Guid NodeB => Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000");

    [Fact]
    public void FindMissing_returns_only_absent_message_ids()
    {
        var store = new InMemoryMessageStore();
        var sessionId = Guid.NewGuid();
        var existing = Guid.NewGuid();
        var missing = Guid.NewGuid();

        store.Add(new ChatMessage { MessageId = existing, SessionId = sessionId, SenderNodeId = NodeA });

        var transport = new FakeTransport(NodeB);
        var sync = new MessageSyncService(store, transport, NodeA);

        var result = sync.FindMissing(sessionId, [existing, missing]);

        Assert.Single(result);
        Assert.Equal(missing, result[0]);
    }

    [Fact]
    public void FindMissing_with_all_known_returns_empty()
    {
        var store = new InMemoryMessageStore();
        var sessionId = Guid.NewGuid();
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        store.Add(new ChatMessage { MessageId = id1, SessionId = sessionId, SenderNodeId = NodeA });
        store.Add(new ChatMessage { MessageId = id2, SessionId = sessionId, SenderNodeId = NodeA });

        var transport = new FakeTransport(NodeB);
        var sync = new MessageSyncService(store, transport, NodeA);

        var result = sync.FindMissing(sessionId, [id1, id2]);

        Assert.Empty(result);
    }

    [Fact]
    public async Task SendInventory_sends_inventory_packet_to_target()
    {
        var store = new InMemoryMessageStore();
        var transport = new FakeTransport(NodeB);
        var sessionId = Guid.NewGuid();
        var sync = new MessageSyncService(store, transport, NodeA);

        await sync.SendInventoryAsync(sessionId, NodeB);

        Assert.Single(transport.Sent);
        Assert.Equal(PacketType.Inventory, transport.Sent[0].Envelope.PacketType);
        Assert.Equal(NodeB, transport.Sent[0].Target);
    }

    [Fact]
    public async Task RequestMissing_sends_missing_request_packet()
    {
        var store = new InMemoryMessageStore();
        var transport = new FakeTransport(NodeB);
        var sessionId = Guid.NewGuid();
        var sync = new MessageSyncService(store, transport, NodeA);
        var missingIds = new[] { Guid.NewGuid(), Guid.NewGuid() };

        await sync.RequestMissingAsync(sessionId, missingIds, NodeB);

        Assert.Single(transport.Sent);
        Assert.Equal(PacketType.MissingRequest, transport.Sent[0].Envelope.PacketType);
    }

    [Fact]
    public async Task RequestMissing_with_empty_list_sends_nothing()
    {
        var store = new InMemoryMessageStore();
        var transport = new FakeTransport(NodeB);
        var sessionId = Guid.NewGuid();
        var sync = new MessageSyncService(store, transport, NodeA);

        await sync.RequestMissingAsync(sessionId, [], NodeB);

        Assert.Empty(transport.Sent);
    }

    [Fact]
    public void Inventory_sync_does_not_duplicate_local_messages()
    {
        var store = new InMemoryMessageStore();
        var sessionId = Guid.NewGuid();
        var msgId = Guid.NewGuid();
        store.Add(new ChatMessage { MessageId = msgId, SessionId = sessionId, SenderNodeId = NodeA });

        var transport = new FakeTransport(NodeB);
        var sync = new MessageSyncService(store, transport, NodeA);

        var missing = sync.FindMissing(sessionId, [msgId]);

        Assert.Empty(missing);
        Assert.Single(store.GetBySession(sessionId));
    }

    [Fact]
    public async Task ResendMessages_only_sends_known_IDs()
    {
        var store = new InMemoryMessageStore();
        var sessionId = Guid.NewGuid();
        var knownId = Guid.NewGuid();
        var unknownId = Guid.NewGuid();

        store.Add(new ChatMessage
        {
            MessageId = knownId,
            SessionId = sessionId,
            SenderNodeId = NodeA,
            Text = "stored"
        });

        var transport = new FakeTransport(NodeB);
        var sync = new MessageSyncService(store, transport, NodeA);

        await sync.ResendMessagesAsync(NodeB, [knownId, unknownId]);

        // Only the known message should be resent; unknown ID is silently skipped
        Assert.Single(transport.Sent);
        Assert.Equal(PacketType.ChatEnvelope, transport.Sent[0].Envelope.PacketType);
        Assert.Equal(NodeB, transport.Sent[0].Target);
    }
}
