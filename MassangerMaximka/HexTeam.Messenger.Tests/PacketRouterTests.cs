using HexTeam.Messenger.Core.Models;
using HexTeam.Messenger.Core.Protocol;
using HexTeam.Messenger.Core.Services;
using HexTeam.Messenger.Core.Storage;
using System.Text.Json;

namespace HexTeam.Messenger.Tests;

public class PacketRouterTests
{
    private static Guid NodeA => Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
    private static Guid NodeB => Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000");
    private static Guid NodeC => Guid.Parse("cccccccc-0000-0000-0000-000000000000");

    private (PacketRouter Router, FakeTransport Transport, InMemoryMessageStore MsgStore) CreateRouter(Guid localNode, params Guid[] peers)
    {
        var seenStore = new InMemorySeenPacketStore();
        var msgStore = new InMemoryMessageStore();
        var transport = new FakeTransport(peers);
        var relay = new RelayService(seenStore, transport, localNode);
        var retry = new RetryPolicy(transport, msgStore);
        var sync = new MessageSyncService(msgStore, transport, localNode);
        var router = new PacketRouter(relay, retry, sync, msgStore, localNode);
        return (router, transport, msgStore);
    }

    private Envelope MakeChatEnvelope(Guid origin, Guid target, string text = "hello")
    {
        var chat = new ChatPacket
        {
            MessageId = Guid.NewGuid(),
            Text = text,
            SentAtUtc = DateTimeOffset.UtcNow
        };
        return new Envelope
        {
            PacketId = Guid.NewGuid(),
            MessageId = chat.MessageId,
            SessionId = Guid.NewGuid(),
            OriginNodeId = origin,
            CurrentSenderNodeId = origin,
            TargetNodeId = target,
            PacketType = PacketType.ChatEnvelope,
            Payload = JsonSerializer.SerializeToUtf8Bytes(chat)
        };
    }

    [Fact]
    public async Task Chat_message_delivered_locally()
    {
        var (router, _, msgStore) = CreateRouter(NodeB, NodeA, NodeC);
        ChatMessage? received = null;
        router.ChatMessageReceived += m => received = m;

        var envelope = MakeChatEnvelope(NodeA, NodeB, "test message");
        await router.HandleIncomingAsync(envelope, NodeA);

        Assert.NotNull(received);
        Assert.Equal("test message", received!.Text);
        Assert.True(msgStore.Contains(envelope.MessageId));
    }

    [Fact]
    public async Task Duplicate_chat_message_not_delivered_twice()
    {
        var (router, _, _) = CreateRouter(NodeB, NodeA);
        int count = 0;
        router.ChatMessageReceived += _ => count++;

        var envelope = MakeChatEnvelope(NodeA, NodeB);
        await router.HandleIncomingAsync(envelope, NodeA);
        await router.HandleIncomingAsync(envelope, NodeA);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Expired_packet_is_dropped()
    {
        var (router, transport, _) = CreateRouter(NodeB, NodeA, NodeC);
        var decisions = new List<RelayDecision>();
        router.PacketRouted += (d, _) => decisions.Add(d);

        var envelope = new Envelope
        {
            OriginNodeId = NodeA,
            CurrentSenderNodeId = NodeA,
            TargetNodeId = NodeC,
            HopCount = 5,
            MaxHops = 5,
            PacketType = PacketType.ChatEnvelope,
            Payload = JsonSerializer.SerializeToUtf8Bytes(new ChatPacket { Text = "expired" })
        };

        await router.HandleIncomingAsync(envelope, NodeA);

        Assert.Contains(RelayDecision.DropHopExceeded, decisions);
        Assert.Empty(transport.Sent);
    }

    [Fact]
    public async Task Ack_packet_acknowledges_pending()
    {
        var (router, transport, msgStore) = CreateRouter(NodeA, NodeB);
        var sentEnvelope = MakeChatEnvelope(NodeA, NodeB);

        var ack = new AckPacket
        {
            AckedPacketId = sentEnvelope.PacketId,
            AckedMessageId = sentEnvelope.MessageId,
            Status = AckStatus.Delivered
        };
        var ackEnvelope = new Envelope
        {
            OriginNodeId = NodeB,
            CurrentSenderNodeId = NodeB,
            TargetNodeId = NodeA,
            PacketType = PacketType.Ack,
            Payload = JsonSerializer.SerializeToUtf8Bytes(ack)
        };

        Guid? ackedId = null;
        router.AckReceived += (id, _) => ackedId = id;
        await router.HandleIncomingAsync(ackEnvelope, NodeB);

        Assert.Equal(sentEnvelope.PacketId, ackedId);
    }

    [Fact]
    public async Task Forward_decision_relays_to_peers()
    {
        var (router, transport, _) = CreateRouter(NodeB, NodeA, NodeC);

        var envelope = MakeChatEnvelope(NodeA, NodeC);
        await router.HandleIncomingAsync(envelope, NodeA);

        Assert.Single(transport.Sent);
        Assert.Equal(NodeC, transport.Sent[0].Target);
    }

    [Fact]
    public async Task PacketRouted_event_fires()
    {
        var (router, _, _) = CreateRouter(NodeB, NodeA);
        var decisions = new List<RelayDecision>();
        router.PacketRouted += (d, _) => decisions.Add(d);

        var envelope = MakeChatEnvelope(NodeA, NodeB);
        await router.HandleIncomingAsync(envelope, NodeA);

        Assert.Single(decisions);
        Assert.Equal(RelayDecision.Deliver, decisions[0]);
    }
}
