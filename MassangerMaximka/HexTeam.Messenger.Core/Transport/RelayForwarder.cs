using System.Collections.Concurrent;
using HexTeam.Messenger.Core.Models;
using Microsoft.Extensions.Logging;

namespace HexTeam.Messenger.Core.Transport;

/// <summary>
/// Relay forwarder that handles A -> B -> C packet forwarding.
/// Tracks seen packets for deduplication and loop protection.
/// </summary>
public sealed class RelayForwarder
{
    private readonly string _nodeId;
    private readonly PeerConnectionService _connectionService;
    private readonly ILogger<RelayForwarder> _logger;
    private readonly ConcurrentDictionary<string, DateTime> _seenPackets = new();
    private static readonly TimeSpan SeenPacketTtl = TimeSpan.FromMinutes(5);

    public bool IsRelayEnabled { get; set; } = true;
    public int RelayedCount { get; private set; }

    public RelayForwarder(string nodeId, PeerConnectionService connectionService, ILogger<RelayForwarder> logger)
    {
        _nodeId = nodeId;
        _connectionService = connectionService;
        _logger = logger;
        _connectionService.EnvelopeReceived += OnEnvelopeReceived;
        _ = PruneSeenPacketsAsync();
    }

    private async Task OnEnvelopeReceived(string fromPeerNodeId, Envelope envelope)
    {
        if (!IsRelayEnabled) return;
        if (envelope.DestinationNodeId == _nodeId) return;
        if (envelope.Type == PacketType.Hello || envelope.Type == PacketType.Discovery) return;

        await ForwardAsync(fromPeerNodeId, envelope);
    }

    public async Task ForwardAsync(string fromPeerNodeId, Envelope envelope)
    {
        if (_seenPackets.ContainsKey(envelope.PacketId))
        {
            _logger.LogDebug("Duplicate packet {Id}, dropping", envelope.PacketId);
            return;
        }

        if (envelope.HopCount >= envelope.MaxHops)
        {
            _logger.LogWarning("Packet {Id} exceeded max hops ({Max}), dropping", envelope.PacketId, envelope.MaxHops);
            return;
        }

        _seenPackets[envelope.PacketId] = DateTime.UtcNow;

        var forwarded = envelope with { HopCount = envelope.HopCount + 1 };

        if (_connectionService.IsConnected(envelope.DestinationNodeId))
        {
            try
            {
                await _connectionService.SendAsync(envelope.DestinationNodeId, forwarded);
                RelayedCount++;
                _logger.LogInformation("Relayed packet {Id} from {From} to {To} (hop {Hop})",
                    envelope.PacketId, fromPeerNodeId, envelope.DestinationNodeId, forwarded.HopCount);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to relay to {NodeId}", envelope.DestinationNodeId);
            }
        }
        else
        {
            foreach (var conn in _connectionService.Connections)
            {
                if (conn.Key == fromPeerNodeId || conn.Key == _nodeId) continue;
                try
                {
                    await _connectionService.SendAsync(conn.Key, forwarded);
                    RelayedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to relay-broadcast to {NodeId}", conn.Key);
                }
            }
            _logger.LogInformation("Broadcast-relayed packet {Id} from {From} (hop {Hop})",
                envelope.PacketId, fromPeerNodeId, forwarded.HopCount);
        }
    }

    private async Task PruneSeenPacketsAsync()
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromMinutes(1));
            var cutoff = DateTime.UtcNow - SeenPacketTtl;
            foreach (var kvp in _seenPackets)
            {
                if (kvp.Value < cutoff)
                    _seenPackets.TryRemove(kvp.Key, out _);
            }
        }
    }
}
