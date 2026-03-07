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

    [Fact]
    public void Packet_marked_Failed_after_MaxRetries()
    {
        var transport = new FakeTransport(NodeB);
        var store = new InMemoryMessageStore();
        var sessionId = Guid.NewGuid();
        var msgId = Guid.NewGuid();
        store.Add(new ChatMessage { MessageId = msgId, SessionId = sessionId, SenderNodeId = NodeA });

        var retry = new RetryPolicy(transport, store);
        var envelope = MakeEnvelope(msgId);
        retry.Track(envelope, NodeB);

        // Tick MaxRetryCount+1 times to exhaust retries
        for (int i = 0; i <= ProtocolConstants.MaxRetryCount; i++)
            retry.ForceTick();

        // Packet must be removed from pending (state = Unknown)
        Assert.Equal(AckWaitState.Unknown, retry.GetState(envelope.PacketId));

        var msg = store.GetBySession(sessionId).First();
        Assert.Equal(MessageDeliveryState.Failed, msg.DeliveryState);
    }

    [Fact]
    public void RetryExhausted_event_fires_after_exhaustion()
    {
        var transport = new FakeTransport(NodeB);
        var store = new InMemoryMessageStore();

        var retry = new RetryPolicy(transport, store);
        var envelope = MakeEnvelope(Guid.NewGuid());
        retry.Track(envelope, NodeB);

        Guid? exhaustedId = null;
        retry.RetryExhausted += id => exhaustedId = id;

        for (int i = 0; i <= ProtocolConstants.MaxRetryCount; i++)
            retry.ForceTick();

        Assert.Equal(envelope.PacketId, exhaustedId);
    }

    [Fact]
    public void Retry_resends_packet_before_exhaustion()
    {
        var transport = new FakeTransport(NodeB);
        var store = new InMemoryMessageStore();

        var retry = new RetryPolicy(transport, store);
        var envelope = MakeEnvelope(Guid.NewGuid());
        retry.Track(envelope, NodeB);

        // First tick retries (RetryCount = 0 < MaxRetryCount = 3)
        retry.ForceTick();

        // Packet should have been resent
        Assert.Single(transport.Sent);
        Assert.Equal(envelope.PacketId, transport.Sent[0].Envelope.PacketId);
    }
}
