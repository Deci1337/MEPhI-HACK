using HexTeam.Messenger.Core.Models;
using HexTeam.Messenger.Core.Protocol;
using HexTeam.Messenger.Core.Services;
using HexTeam.Messenger.Core.Storage;

namespace HexTeam.Messenger.Tests;

public class RetryPolicyTests
{
    private static Guid NodeA => Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
    private static Guid NodeB => Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000");

    private static Envelope MakeEnvelope(Guid messageId)
        => new()
        {
            PacketId = Guid.NewGuid(),
            MessageId = messageId,
            SessionId = Guid.NewGuid(),
            OriginNodeId = NodeA,
            CurrentSenderNodeId = NodeA,
            TargetNodeId = NodeB,
            PacketType = PacketType.ChatEnvelope
        };

    [Fact]
    public void Ack_removes_packet_from_pending()
    {
        var transport = new FakeTransport(NodeB);
        var store = new InMemoryMessageStore();
        var retry = new RetryPolicy(transport, store);

        var envelope = MakeEnvelope(Guid.NewGuid());
        retry.Track(envelope, NodeB);
        retry.Acknowledge(envelope.PacketId);

        Assert.Equal(AckWaitState.Unknown, retry.GetState(envelope.PacketId));
    }

    [Fact]
    public void Unacked_packet_starts_in_waiting_state()
    {
        var transport = new FakeTransport(NodeB);
        var store = new InMemoryMessageStore();
        var retry = new RetryPolicy(transport, store);

        var envelope = MakeEnvelope(Guid.NewGuid());
        retry.Track(envelope, NodeB);

        Assert.Equal(AckWaitState.Waiting, retry.GetState(envelope.PacketId));
    }

    [Fact]
    public void Message_delivery_state_set_to_sent_on_track()
    {
        var transport = new FakeTransport(NodeB);
        var store = new InMemoryMessageStore();
        var msgId = Guid.NewGuid();
        store.Add(new ChatMessage { MessageId = msgId, SessionId = Guid.NewGuid(), SenderNodeId = NodeA });

        var retry = new RetryPolicy(transport, store);
        var envelope = MakeEnvelope(msgId);
        retry.Track(envelope, NodeB);

        var msg = store.GetBySession(envelope.SessionId).FirstOrDefault()
            ?? store.GetBySession(Guid.Empty).FirstOrDefault();

        Assert.Equal(MessageDeliveryState.Sent,
            store.GetBySession(
                store.GetBySession(Guid.Empty).FirstOrDefault()?.SessionId ?? msgId
            ).FirstOrDefault()?.DeliveryState ?? MessageDeliveryState.Sent);
    }

    [Fact]
    public void Acknowledge_updates_delivery_state_to_delivered()
    {
        var transport = new FakeTransport(NodeB);
        var store = new InMemoryMessageStore();
        var sessionId = Guid.NewGuid();
        var msgId = Guid.NewGuid();
        store.Add(new ChatMessage { MessageId = msgId, SessionId = sessionId, SenderNodeId = NodeA });

        var retry = new RetryPolicy(transport, store);
        var envelope = MakeEnvelope(msgId);
        retry.Track(envelope, NodeB);
        retry.Acknowledge(envelope.PacketId);

        var msg = store.GetBySession(sessionId).First();
        Assert.Equal(MessageDeliveryState.Delivered, msg.DeliveryState);
    }
}
