using HexTeam.Messenger.Core.Models;
using HexTeam.Messenger.Core.Protocol;
using HexTeam.Messenger.Core.Services;
using HexTeam.Messenger.Core.Storage;

namespace HexTeam.Messenger.Tests;

public class ReconnectFlowTests
{
    private static Guid NodeA => Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
    private static Guid NodeB => Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000");

    [Fact]
    public async Task Reconnect_triggers_automatic_inventory_sync()
    {
        var identityA = new NodeIdentity(NodeA, "NodeA");
        var identityB = new NodeIdentity(NodeB, "NodeB");
        var storeA = new InMemoryMessageStore();
        var storeB = new InMemoryMessageStore();
        var seenA = new InMemorySeenPacketStore();
        var seenB = new InMemorySeenPacketStore();
        var transportA = new FakeTransport(NodeB);
        var transportB = new FakeTransport(NodeA);

        var verifierA = new HandshakeVerifier(identityA);
        var verifierB = new HandshakeVerifier(identityB);
        var loggerA = new Microsoft.Extensions.Logging.Abstractions.NullLogger<PacketRouter>();
        var loggerB = new Microsoft.Extensions.Logging.Abstractions.NullLogger<PacketRouter>();

        var relayA = new RelayService(seenA, transportA, NodeA);
        var relayB = new RelayService(seenB, transportB, NodeB);
        var retryA = new RetryPolicy(transportA, storeA);
        var retryB = new RetryPolicy(transportB, storeB);
        var syncA = new MessageSyncService(storeA, transportA, NodeA);
        var syncB = new MessageSyncService(storeB, transportB, NodeB);

        var routerA = new PacketRouter(relayA, retryA, syncA, storeA, verifierA, loggerA, NodeA);
        var routerB = new PacketRouter(relayB, retryB, syncB, storeB, verifierB, loggerB, NodeB);

        var sessionId = Guid.NewGuid();

        await routerA.OnPeerConnectedAsync(NodeB, sessionId);

        Assert.Single(transportA.Sent);
        Assert.Equal(PacketType.Inventory, transportA.Sent[0].Envelope.PacketType);
        Assert.Equal(NodeB, transportA.Sent[0].Target);
    }

    [Fact]
    public async Task Full_reconnect_flow_syncs_missing_messages()
    {
        var identityA = new NodeIdentity(NodeA, "NodeA");
        var identityB = new NodeIdentity(NodeB, "NodeB");
        var storeA = new InMemoryMessageStore();
        var storeB = new InMemoryMessageStore();
        var seenA = new InMemorySeenPacketStore();
        var seenB = new InMemorySeenPacketStore();
        var transportA = new FakeTransport(NodeB);
        var transportB = new FakeTransport(NodeA);

        var verifierA = new HandshakeVerifier(identityA);
        var verifierB = new HandshakeVerifier(identityB);
        var loggerA = new Microsoft.Extensions.Logging.Abstractions.NullLogger<PacketRouter>();
        var loggerB = new Microsoft.Extensions.Logging.Abstractions.NullLogger<PacketRouter>();

        var relayA = new RelayService(seenA, transportA, NodeA);
        var relayB = new RelayService(seenB, transportB, NodeB);
        var retryA = new RetryPolicy(transportA, storeA);
        var retryB = new RetryPolicy(transportB, storeB);
        var syncA = new MessageSyncService(storeA, transportA, NodeA);
        var syncB = new MessageSyncService(storeB, transportB, NodeB);

        var routerA = new PacketRouter(relayA, retryA, syncA, storeA, verifierA, loggerA, NodeA);
        var routerB = new PacketRouter(relayB, retryB, syncB, storeB, verifierB, loggerB, NodeB);

        var sessionId = Guid.NewGuid();
        var msg1Id = Guid.NewGuid();
        var msg2Id = Guid.NewGuid();
        var msg3Id = Guid.NewGuid();

        storeA.Add(new ChatMessage
        {
            MessageId = msg1Id,
            SessionId = sessionId,
            SenderNodeId = NodeA,
            Text = "Message 1",
            SentAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        });

        storeA.Add(new ChatMessage
        {
            MessageId = msg2Id,
            SessionId = sessionId,
            SenderNodeId = NodeA,
            Text = "Message 2",
            SentAtUtc = DateTimeOffset.UtcNow.AddMinutes(-3)
        });

        storeA.Add(new ChatMessage
        {
            MessageId = msg3Id,
            SessionId = sessionId,
            SenderNodeId = NodeA,
            Text = "Message 3",
            SentAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
        });

        storeB.Add(new ChatMessage
        {
            MessageId = msg1Id,
            SessionId = sessionId,
            SenderNodeId = NodeA,
            Text = "Message 1",
            SentAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        });

        await routerA.OnPeerReconnectedAsync(NodeB, sessionId);

        Assert.Single(transportA.Sent);
        var inventoryEnvelope = transportA.Sent[0].Envelope;
        Assert.Equal(PacketType.Inventory, inventoryEnvelope.PacketType);

        var inventory = System.Text.Json.JsonSerializer.Deserialize<InventoryPacket>(inventoryEnvelope.Payload);
        Assert.NotNull(inventory);
        Assert.Equal(3, inventory.MessageIds.Count);
        Assert.Contains(msg1Id, inventory.MessageIds);
        Assert.Contains(msg2Id, inventory.MessageIds);
        Assert.Contains(msg3Id, inventory.MessageIds);

        await routerB.HandleIncomingAsync(inventoryEnvelope, NodeA);

        Assert.Single(transportB.Sent);
        var missingRequestEnvelope = transportB.Sent[0].Envelope;
        Assert.Equal(PacketType.MissingRequest, missingRequestEnvelope.PacketType);

        var missingRequest = System.Text.Json.JsonSerializer.Deserialize<MissingRequestPacket>(missingRequestEnvelope.Payload);
        Assert.NotNull(missingRequest);
        Assert.Equal(2, missingRequest.MissingMessageIds.Count);
        Assert.Contains(msg2Id, missingRequest.MissingMessageIds);
        Assert.Contains(msg3Id, missingRequest.MissingMessageIds);
        Assert.DoesNotContain(msg1Id, missingRequest.MissingMessageIds);

        await routerA.HandleIncomingAsync(missingRequestEnvelope, NodeB);

        var chatEnvelopes = transportA.Sent.Skip(1).Where(s => s.Envelope.PacketType == PacketType.ChatEnvelope).ToList();
        Assert.Equal(2, chatEnvelopes.Count);

        foreach (var chatEnvelope in chatEnvelopes)
        {
            await routerB.HandleIncomingAsync(chatEnvelope.Envelope, NodeA);
        }

        Assert.Equal(3, storeB.GetBySession(sessionId).Count);
        Assert.True(storeB.Contains(msg1Id));
        Assert.True(storeB.Contains(msg2Id));
        Assert.True(storeB.Contains(msg3Id));
    }

    [Fact]
    public async Task Reconnect_prevents_parallel_syncs()
    {
        var identityA = new NodeIdentity(NodeA, "NodeA");
        var storeA = new InMemoryMessageStore();
        var seenA = new InMemorySeenPacketStore();
        var gate = new TaskCompletionSource();
        var transportA = new GatedFakeTransport(gate.Task, NodeB);

        var verifierA = new HandshakeVerifier(identityA);
        var loggerA = new Microsoft.Extensions.Logging.Abstractions.NullLogger<PacketRouter>();

        var relayA = new RelayService(seenA, transportA, NodeA);
        var retryA = new RetryPolicy(transportA, storeA);
        var syncA = new MessageSyncService(storeA, transportA, NodeA);

        var routerA = new PacketRouter(relayA, retryA, syncA, storeA, verifierA, loggerA, NodeA);

        var sessionId = Guid.NewGuid();

        var task1 = routerA.OnPeerReconnectedAsync(NodeB, sessionId);

        await Task.Delay(50);

        var task2 = routerA.OnPeerReconnectedAsync(NodeB, sessionId);
        await task2;

        gate.SetResult();
        await task1;

        Assert.Single(transportA.Sent);
        Assert.Equal(PacketType.Inventory, transportA.Sent[0].Envelope.PacketType);
    }

    [Fact]
    public async Task Sync_messages_arrive_in_correct_order()
    {
        var identityA = new NodeIdentity(NodeA, "NodeA");
        var identityB = new NodeIdentity(NodeB, "NodeB");
        var storeA = new InMemoryMessageStore();
        var storeB = new InMemoryMessageStore();
        var seenA = new InMemorySeenPacketStore();
        var seenB = new InMemorySeenPacketStore();
        var transportA = new FakeTransport(NodeB);
        var transportB = new FakeTransport(NodeA);

        var verifierA = new HandshakeVerifier(identityA);
        var verifierB = new HandshakeVerifier(identityB);
        var loggerA = new Microsoft.Extensions.Logging.Abstractions.NullLogger<PacketRouter>();
        var loggerB = new Microsoft.Extensions.Logging.Abstractions.NullLogger<PacketRouter>();

        var relayA = new RelayService(seenA, transportA, NodeA);
        var relayB = new RelayService(seenB, transportB, NodeB);
        var retryA = new RetryPolicy(transportA, storeA);
        var retryB = new RetryPolicy(transportB, storeB);
        var syncA = new MessageSyncService(storeA, transportA, NodeA);
        var syncB = new MessageSyncService(storeB, transportB, NodeB);

        var routerA = new PacketRouter(relayA, retryA, syncA, storeA, verifierA, loggerA, NodeA);
        var routerB = new PacketRouter(relayB, retryB, syncB, storeB, verifierB, loggerB, NodeB);

        var sessionId = Guid.NewGuid();
        var baseTime = DateTimeOffset.UtcNow;

        var messages = new[]
        {
            new ChatMessage { MessageId = Guid.NewGuid(), SessionId = sessionId, SenderNodeId = NodeA, Text = "Msg 1", SentAtUtc = baseTime.AddSeconds(-10) },
            new ChatMessage { MessageId = Guid.NewGuid(), SessionId = sessionId, SenderNodeId = NodeA, Text = "Msg 2", SentAtUtc = baseTime.AddSeconds(-8) },
            new ChatMessage { MessageId = Guid.NewGuid(), SessionId = sessionId, SenderNodeId = NodeA, Text = "Msg 3", SentAtUtc = baseTime.AddSeconds(-6) }
        };

        foreach (var msg in messages)
        {
            storeA.Add(msg);
        }

        storeB.Add(messages[0]);

        await routerA.OnPeerReconnectedAsync(NodeB, sessionId);
        var inventoryEnvelope = transportA.Sent[0].Envelope;
        await routerB.HandleIncomingAsync(inventoryEnvelope, NodeA);

        var missingRequestEnvelope = transportB.Sent[0].Envelope;
        await routerA.HandleIncomingAsync(missingRequestEnvelope, NodeB);

        var chatEnvelopes = transportA.Sent.Skip(1)
            .Where(s => s.Envelope.PacketType == PacketType.ChatEnvelope)
            .ToList();

        Assert.Equal(2, chatEnvelopes.Count);

        foreach (var sent in chatEnvelopes)
        {
            await routerB.HandleIncomingAsync(sent.Envelope, NodeA);
        }

        var receivedMessages = storeB.GetBySession(sessionId)
            .OrderBy(m => m.SentAtUtc)
            .ToList();

        Assert.Equal(3, receivedMessages.Count);
        Assert.Equal("Msg 1", receivedMessages[0].Text);
        Assert.Equal("Msg 2", receivedMessages[1].Text);
        Assert.Equal("Msg 3", receivedMessages[2].Text);
    }

    [Fact]
    public async Task Sync_does_not_duplicate_existing_messages()
    {
        var identityA = new NodeIdentity(NodeA, "NodeA");
        var identityB = new NodeIdentity(NodeB, "NodeB");
        var storeA = new InMemoryMessageStore();
        var storeB = new InMemoryMessageStore();
        var seenA = new InMemorySeenPacketStore();
        var seenB = new InMemorySeenPacketStore();
        var transportA = new FakeTransport(NodeB);
        var transportB = new FakeTransport(NodeA);

        var verifierA = new HandshakeVerifier(identityA);
        var verifierB = new HandshakeVerifier(identityB);
        var loggerA = new Microsoft.Extensions.Logging.Abstractions.NullLogger<PacketRouter>();
        var loggerB = new Microsoft.Extensions.Logging.Abstractions.NullLogger<PacketRouter>();

        var relayA = new RelayService(seenA, transportA, NodeA);
        var relayB = new RelayService(seenB, transportB, NodeB);
        var retryA = new RetryPolicy(transportA, storeA);
        var retryB = new RetryPolicy(transportB, storeB);
        var syncA = new MessageSyncService(storeA, transportA, NodeA);
        var syncB = new MessageSyncService(storeB, transportB, NodeB);

        var routerA = new PacketRouter(relayA, retryA, syncA, storeA, verifierA, loggerA, NodeA);
        var routerB = new PacketRouter(relayB, retryB, syncB, storeB, verifierB, loggerB, NodeB);

        var sessionId = Guid.NewGuid();
        var msgId = Guid.NewGuid();

        var message = new ChatMessage
        {
            MessageId = msgId,
            SessionId = sessionId,
            SenderNodeId = NodeA,
            Text = "Existing message",
            SentAtUtc = DateTimeOffset.UtcNow
        };

        storeA.Add(message);
        storeB.Add(message);

        await routerA.OnPeerReconnectedAsync(NodeB, sessionId);
        var inventoryEnvelope = transportA.Sent[0].Envelope;
        await routerB.HandleIncomingAsync(inventoryEnvelope, NodeA);

        Assert.DoesNotContain(transportB.Sent, s => s.Envelope.PacketType == PacketType.MissingRequest);
        Assert.Single(storeB.GetBySession(sessionId));
    }
}
