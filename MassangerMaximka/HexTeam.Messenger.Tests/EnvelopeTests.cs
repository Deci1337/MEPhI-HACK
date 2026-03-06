using HexTeam.Messenger.Core.Models;
using HexTeam.Messenger.Core.Protocol;

namespace HexTeam.Messenger.Tests;

public class EnvelopeTests
{
    [Fact]
    public void IsExpired_true_when_hop_count_equals_max_hops()
    {
        var env = new Envelope { HopCount = 5, MaxHops = 5 };
        Assert.True(env.IsExpired);
    }

    [Fact]
    public void IsExpired_false_when_hop_count_below_max_hops()
    {
        var env = new Envelope { HopCount = 4, MaxHops = 5 };
        Assert.False(env.IsExpired);
    }

    [Fact]
    public void WithNextHop_increments_hop_count()
    {
        var origin = Guid.NewGuid();
        var relay = Guid.NewGuid();
        var env = new Envelope
        {
            PacketId = Guid.NewGuid(),
            OriginNodeId = origin,
            CurrentSenderNodeId = origin,
            HopCount = 2,
            MaxHops = 5,
            PacketType = PacketType.ChatEnvelope
        };

        var next = env.WithNextHop(relay);

        Assert.Equal(3, next.HopCount);
        Assert.Equal(relay, next.CurrentSenderNodeId);
        Assert.Equal(origin, next.OriginNodeId);
        Assert.Equal(env.PacketId, next.PacketId);
    }

    [Fact]
    public void WithNextHop_preserves_packet_id_and_message_id()
    {
        var packetId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var env = new Envelope { PacketId = packetId, MessageId = messageId };

        var next = env.WithNextHop(Guid.NewGuid());

        Assert.Equal(packetId, next.PacketId);
        Assert.Equal(messageId, next.MessageId);
    }
}
