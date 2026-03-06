using HexTeam.Messenger.Core.Models;
using HexTeam.Messenger.Core.Protocol;
using HexTeam.Messenger.Core.Services;
using HexTeam.Messenger.Core.Storage;

namespace HexTeam.Messenger.Tests;

public class RelayServiceTests
{
    private static Guid NodeA => Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
    private static Guid NodeB => Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000");
    private static Guid NodeC => Guid.Parse("cccccccc-0000-0000-0000-000000000000");
    private static Guid NodeD => Guid.Parse("dddddddd-0000-0000-0000-000000000000");

    private static Envelope MakeEnvelope(Guid origin, Guid target, int hopCount = 0, int maxHops = 5)
        => new()
        {
            PacketId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            OriginNodeId = origin,
            CurrentSenderNodeId = origin,
            TargetNodeId = target,
            HopCount = hopCount,
            MaxHops = maxHops,
            PacketType = PacketType.ChatEnvelope,
            Payload = []
        };

    [Fact]
    public void Duplicate_packet_is_dropped()
    {
        var store = new InMemorySeenPacketStore();
        var transport = new FakeTransport(NodeC);
        var relay = new RelayService(store, transport, NodeB);
        var envelope = MakeEnvelope(NodeA, NodeC);

        var first = relay.ShouldRelay(envelope, NodeA);
        var second = relay.ShouldRelay(envelope, NodeA);

        Assert.Equal(RelayDecision.Forward, first);
        Assert.Equal(RelayDecision.DropDuplicate, second);
    }

    [Fact]
    public async Task Packet_forwarded_at_most_once()
    {
        var store = new InMemorySeenPacketStore();
        var transport = new FakeTransport(NodeC);
        var relay = new RelayService(store, transport, NodeB);
        var envelope = MakeEnvelope(NodeA, NodeC);

        await relay.ProcessAsync(envelope, NodeA);
        await relay.ProcessAsync(envelope, NodeA);

        Assert.Single(transport.Sent);
    }

    [Fact]
    public void Packet_dropped_when_hop_count_at_max()
    {
        var store = new InMemorySeenPacketStore();
        var transport = new FakeTransport(NodeC);
        var relay = new RelayService(store, transport, NodeB);
        var envelope = MakeEnvelope(NodeA, NodeC, hopCount: 5, maxHops: 5);

        var decision = relay.ShouldRelay(envelope, NodeA);

        Assert.Equal(RelayDecision.DropHopExceeded, decision);
        Assert.Empty(transport.Sent);
    }

    [Fact]
    public async Task Relay_does_not_send_back_to_sender()
    {
        var store = new InMemorySeenPacketStore();
        var transport = new FakeTransport(NodeA, NodeC, NodeD);
        var relay = new RelayService(store, transport, NodeB);
        var envelope = MakeEnvelope(NodeA, NodeD);

        await relay.ProcessAsync(envelope, NodeA);

        Assert.All(transport.Sent, s => Assert.NotEqual(NodeA, s.Target));
    }

    [Fact]
    public void Packet_delivered_locally_not_forwarded()
    {
        var store = new InMemorySeenPacketStore();
        var transport = new FakeTransport(NodeA, NodeC);
        var relay = new RelayService(store, transport, NodeB);
        var envelope = MakeEnvelope(NodeA, NodeB);

        var decision = relay.ShouldRelay(envelope, NodeA);

        Assert.Equal(RelayDecision.Deliver, decision);
        Assert.Empty(transport.Sent);
    }

    [Fact]
    public async Task Relay_increments_hop_count()
    {
        var store = new InMemorySeenPacketStore();
        var transport = new FakeTransport(NodeC);
        var relay = new RelayService(store, transport, NodeB);
        var envelope = MakeEnvelope(NodeA, NodeC, hopCount: 2);

        await relay.ProcessAsync(envelope, NodeA);

        Assert.Single(transport.Sent);
        Assert.Equal(3, transport.Sent[0].Envelope.HopCount);
    }
}
