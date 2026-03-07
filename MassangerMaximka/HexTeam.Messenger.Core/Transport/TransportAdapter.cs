using HexTeam.Messenger.Core.Abstractions;
using HexTeam.Messenger.Core.Protocol;
using System.Text.Json;

namespace HexTeam.Messenger.Core.Transport;

/// <summary>
/// Bridges ITransport (Core protocol Envelope) to PeerConnectionService (TransportEnvelope).
/// Core Envelopes are serialised as JSON into TransportEnvelope.Payload and sent as Relay packets.
/// </summary>
public sealed class TransportAdapter : ITransport, IDisposable
{
    private readonly PeerConnectionService _connections;

    public event Action<Envelope, Guid>? PacketReceived;

    public TransportAdapter(PeerConnectionService connections)
    {
        _connections = connections;
        _connections.EnvelopeReceived += OnTransportEnvelopeReceived;
    }

    public async Task SendAsync(Envelope envelope, Guid targetNodeId, CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope);
        var transport = new TransportEnvelope
        {
            PacketId = envelope.PacketId.ToString("N")[..16],
            Type = TransportPacketType.Relay,
            SourceNodeId = envelope.OriginNodeId.ToString(),
            DestinationNodeId = targetNodeId.ToString(),
            HopCount = envelope.HopCount,
            MaxHops = envelope.MaxHops,
            Payload = payload
        };

        var targetStr = targetNodeId.ToString();
        if (_connections.IsConnected(targetStr))
            await _connections.SendAsync(targetStr, transport, ct);
        else
            await _connections.BroadcastAsync(transport, ct);
    }

    public IReadOnlyList<Guid> GetConnectedPeers() =>
        _connections.Connections.Keys
            .Select(k => Guid.TryParse(k, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToList();

    private Task OnTransportEnvelopeReceived(string fromPeerNodeId, TransportEnvelope transportEnvelope)
    {
        if (transportEnvelope.Type != TransportPacketType.Relay)
            return Task.CompletedTask;

        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(transportEnvelope.Payload);
            if (envelope != null && Guid.TryParse(fromPeerNodeId, out var fromGuid))
                PacketReceived?.Invoke(envelope, fromGuid);
        }
        catch
        {
            // malformed payload — drop silently
        }

        return Task.CompletedTask;
    }

    public void Dispose() =>
        _connections.EnvelopeReceived -= OnTransportEnvelopeReceived;
}
