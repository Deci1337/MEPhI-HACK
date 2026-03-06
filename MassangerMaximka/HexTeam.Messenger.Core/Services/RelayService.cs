using HexTeam.Messenger.Core.Abstractions;
using HexTeam.Messenger.Core.Models;
using HexTeam.Messenger.Core.Protocol;
using HexTeam.Messenger.Core.Storage;

namespace HexTeam.Messenger.Core.Services;

public sealed class RelayService
{
    private readonly ISeenPacketStore _seenPackets;
    private readonly ITransport _transport;
    private readonly Guid _localNodeId;

    public RelayService(ISeenPacketStore seenPackets, ITransport transport, Guid localNodeId)
    {
        _seenPackets = seenPackets;
        _transport = transport;
        _localNodeId = localNodeId;
    }

    public RelayDecision ShouldRelay(Envelope envelope, Guid receivedFromNodeId)
    {
        if (envelope.IsExpired)
            return RelayDecision.DropHopExceeded;

        if (!_seenPackets.TryMarkSeen(envelope.PacketId))
            return RelayDecision.DropDuplicate;

        if (envelope.TargetNodeId == _localNodeId)
            return RelayDecision.Deliver;

        return RelayDecision.Forward;
    }

    public async Task ProcessAsync(Envelope envelope, Guid receivedFromNodeId, CancellationToken ct = default)
    {
        var decision = ShouldRelay(envelope, receivedFromNodeId);

        if (decision == RelayDecision.Forward)
        {
            var forwarded = envelope.WithNextHop(_localNodeId);
            var peers = _transport.GetConnectedPeers()
                .Where(p => p != receivedFromNodeId && p != envelope.OriginNodeId)
                .ToList();

            foreach (var peer in peers)
                await _transport.SendAsync(forwarded, peer, ct);
        }
    }
}

public enum RelayDecision
{
    Deliver,
    Forward,
    DropDuplicate,
    DropHopExceeded
}
