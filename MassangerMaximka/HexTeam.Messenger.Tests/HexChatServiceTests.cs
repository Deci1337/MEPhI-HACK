using HexTeam.Messenger.Core.Models;
using HexTeam.Messenger.Core.Protocol;
using HexTeam.Messenger.Core.Services;
using HexTeam.Messenger.Core.Storage;
using HexTeam.Messenger.Core.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace HexTeam.Messenger.Tests;

public class HexChatServiceTests
{
    private static Guid NodeA => Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
    private static Guid NodeB => Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000");

    private (HexChatService Service, PacketRouter Router, FakeTransport Transport, InMemoryMessageStore MsgStore, RetryPolicy Retry) CreateService()
    {
        var seenStore = new InMemorySeenPacketStore();
        var msgStore = new InMemoryMessageStore();
        var transport = new FakeTransport(NodeB);
        var relay = new RelayService(seenStore, transport, NodeA);
        var retry = new RetryPolicy(transport, msgStore);
        var sync = new MessageSyncService(msgStore, transport, NodeA);
        var identity = new NodeIdentity(NodeA, "test");
        var verifier = new HandshakeVerifier(identity);
        var loggerRouter = NullLogger<PacketRouter>.Instance;
        var router = new PacketRouter(relay, retry, sync, msgStore, verifier, loggerRouter, NodeA);
        
        var loggerSvc = NullLogger<HexChatService>.Instance;
        var svc = new HexChatService(transport, router, identity, msgStore, retry, loggerSvc);
        
        return (svc, router, transport, msgStore, retry);
    }

    [Fact]
    public async Task SendMessageAsync_creates_valid_Envelope_and_calls_ITransport()
    {
        var (svc, _, transport, _, _) = CreateService();

        var result = await svc.SendMessageAsync(NodeB.ToString(), "Hello");

        Assert.Equal(DeliveryStatus.Sent, result.Status);
        Assert.Single(transport.Sent);
        
        var sent = transport.Sent[0];
        Assert.Equal(NodeB, sent.Target);
        Assert.Equal(PacketType.ChatEnvelope, sent.Envelope.PacketType);
        Assert.Equal(NodeA, sent.Envelope.OriginNodeId);
        Assert.Equal(NodeB, sent.Envelope.TargetNodeId);

        var chatPacket = JsonSerializer.Deserialize<ChatPacket>(sent.Envelope.Payload);
        Assert.NotNull(chatPacket);
        Assert.Equal("Hello", chatPacket.Text);
        Assert.Equal(result.MessageId, chatPacket.MessageId.ToString());
    }

    [Fact]
    public async Task AckReceived_raises_DeliveryStatusChanged()
    {
        var (svc, router, transport, _, _) = CreateService();

        DeliveryStatus? reportedStatus = null;
        string? reportedMessageId = null;
        svc.DeliveryStatusChanged += (msgId, status) =>
        {
            reportedMessageId = msgId;
            reportedStatus = status;
        };

        var result = await svc.SendMessageAsync(NodeB.ToString(), "Hello");
        var sentEnvelope = transport.Sent[0].Envelope;

        var ackPacket = new AckPacket
        {
            AckedPacketId = sentEnvelope.PacketId,
            Status = AckStatus.Delivered
        };

        var ackEnvelope = new Envelope
        {
            PacketId = Guid.NewGuid(),
            OriginNodeId = NodeB,
            CurrentSenderNodeId = NodeB,
            TargetNodeId = NodeA,
            PacketType = PacketType.Ack,
            Payload = JsonSerializer.SerializeToUtf8Bytes(ackPacket)
        };

        await router.HandleIncomingAsync(ackEnvelope, NodeB);

        Assert.Equal(result.MessageId, reportedMessageId);
        Assert.Equal(DeliveryStatus.Delivered, reportedStatus);
    }

    [Fact]
    public async Task SessionId_is_stable_for_same_peer()
    {
        var (svc, _, transport, _, _) = CreateService();

        await svc.SendMessageAsync(NodeB.ToString(), "Msg 1");
        var session1 = transport.Sent[0].Envelope.SessionId;

        await svc.SendMessageAsync(NodeB.ToString(), "Msg 2");
        var session2 = transport.Sent[1].Envelope.SessionId;

        Assert.NotEqual(Guid.Empty, session1);
        Assert.Equal(session1, session2);
    }
}
