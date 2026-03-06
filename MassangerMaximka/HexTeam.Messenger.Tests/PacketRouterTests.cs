using HexTeam.Messenger.Core.Models;
using HexTeam.Messenger.Core.Protocol;
using HexTeam.Messenger.Core.Services;
using HexTeam.Messenger.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
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
        var identity = new NodeIdentity(localNode, "test");
        var verifier = new HandshakeVerifier(identity);
        var logger = NullLogger<PacketRouter>.Instance;
        var router = new PacketRouter(relay, retry, sync, msgStore, verifier, logger, localNode);
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

    [Fact]
    public async Task Rejects_packet_with_empty_PacketId()
    {
        var (router, _, _) = CreateRouter(NodeB, NodeA);
        int count = 0;
        router.ChatMessageReceived += _ => count++;

        var envelope = new Envelope
        {
            PacketId = Guid.Empty,
            OriginNodeId = NodeA,
            CurrentSenderNodeId = NodeA,
            TargetNodeId = NodeB,
            PacketType = PacketType.ChatEnvelope,
            Payload = JsonSerializer.SerializeToUtf8Bytes(new ChatPacket { Text = "bad" })
        };

        await router.HandleIncomingAsync(envelope, NodeA);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Rejects_packet_with_empty_OriginNodeId()
    {
        var (router, _, _) = CreateRouter(NodeB, NodeA);
        int count = 0;
        router.ChatMessageReceived += _ => count++;

        var envelope = new Envelope
        {
            OriginNodeId = Guid.Empty,
            CurrentSenderNodeId = NodeA,
            TargetNodeId = NodeB,
            PacketType = PacketType.ChatEnvelope,
            Payload = JsonSerializer.SerializeToUtf8Bytes(new ChatPacket { Text = "bad" })
        };

        await router.HandleIncomingAsync(envelope, NodeA);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Rejects_self_originated_packet_with_zero_hops()
    {
        var (router, _, _) = CreateRouter(NodeB, NodeA);
        int count = 0;
        router.ChatMessageReceived += _ => count++;

        var envelope = new Envelope
        {
            OriginNodeId = NodeB,
            CurrentSenderNodeId = NodeB,
            TargetNodeId = NodeB,
            HopCount = 0,
            PacketType = PacketType.ChatEnvelope,
            Payload = JsonSerializer.SerializeToUtf8Bytes(new ChatPacket { Text = "self" })
        };

        await router.HandleIncomingAsync(envelope, NodeA);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Handles_malformed_payload_without_crash()
    {
        var (router, _, _) = CreateRouter(NodeB, NodeA);
        int count = 0;
        router.ChatMessageReceived += _ => count++;

        var envelope = new Envelope
        {
            OriginNodeId = NodeA,
            CurrentSenderNodeId = NodeA,
            TargetNodeId = NodeB,
            PacketType = PacketType.ChatEnvelope,
            Payload = "{{not valid json"u8.ToArray()
        };

        await router.HandleIncomingAsync(envelope, NodeA);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Unknown_PacketType_is_rejected()
    {
        var (router, _, _) = CreateRouter(NodeB, NodeA);
        var routed = new List<RelayDecision>();
        router.PacketRouted += (d, _) => routed.Add(d);

        var envelope = new Envelope
        {
            OriginNodeId = NodeA,
            CurrentSenderNodeId = NodeA,
            TargetNodeId = NodeB,
            PacketType = (PacketType)255,
            Payload = []
        };

        await router.HandleIncomingAsync(envelope, NodeA);
        Assert.Empty(routed);
    }

    [Fact]
    public async Task MissingRequest_triggers_resend()
    {
        var (router, transport, msgStore) = CreateRouter(NodeB, NodeA);
        var sessionId = Guid.NewGuid();
        var msgId = Guid.NewGuid();

        msgStore.Add(new ChatMessage
        {
            MessageId = msgId,
            SessionId = sessionId,
            SenderNodeId = NodeB,
            Text = "stored message",
            SentAtUtc = DateTimeOffset.UtcNow
        });

        var req = new MissingRequestPacket
        {
            SessionId = sessionId,
            MissingMessageIds = [msgId]
        };
        var envelope = new Envelope
        {
            OriginNodeId = NodeA,
            CurrentSenderNodeId = NodeA,
            TargetNodeId = NodeB,
            PacketType = PacketType.MissingRequest,
            Payload = JsonSerializer.SerializeToUtf8Bytes(req)
        };

        await router.HandleIncomingAsync(envelope, NodeA);

        Assert.Single(transport.Sent);
        Assert.Equal(PacketType.ChatEnvelope, transport.Sent[0].Envelope.PacketType);
        Assert.Equal(NodeA, transport.Sent[0].Target);
    }

    [Fact]
    public async Task OnPeerReconnected_sends_inventory()
    {
        var (router, transport, _) = CreateRouter(NodeB, NodeA);
        var sessionId = Guid.NewGuid();

        await router.OnPeerReconnectedAsync(NodeA, sessionId);

        Assert.Single(transport.Sent);
        Assert.Equal(PacketType.Inventory, transport.Sent[0].Envelope.PacketType);
    }

    [Fact]
    public void TrackForAck_tracks_chat_packets()
    {
        var (router, _, _) = CreateRouter(NodeA, NodeB);
        var envelope = MakeChatEnvelope(NodeA, NodeB);

        router.TrackForAck(envelope, NodeB);
    }

    [Fact]
    public void RequiresAck_true_for_ChatEnvelope()
    {
        Assert.True(ProtocolConstants.RequiresAck(PacketType.ChatEnvelope));
    }

    [Fact]
    public void RequiresAck_false_for_Ack()
    {
        Assert.False(ProtocolConstants.RequiresAck(PacketType.Ack));
    }

    [Fact]
    public void RequiresAck_false_for_Inventory()
    {
        Assert.False(ProtocolConstants.RequiresAck(PacketType.Inventory));
    }
}
