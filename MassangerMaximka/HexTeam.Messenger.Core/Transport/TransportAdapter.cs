using HexTeam.Messenger.Core.Abstractions;
using HexTeam.Messenger.Core.Protocol;
using System.Text.Json;
using Microsoft.Extensions.Logging;

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
        if (transportEnvelope.Type == TransportPacketType.Relay)
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<Envelope>(transportEnvelope.Payload);
                if (envelope != null && Guid.TryParse(fromPeerNodeId, out var fromGuid))
                    PacketReceived?.Invoke(envelope, fromGuid);
            }
            catch { }
        }
        else if (transportEnvelope.Type == TransportPacketType.Chat)
        {
            TryEmitChatAsEnvelope(fromPeerNodeId, transportEnvelope);
        }

        return Task.CompletedTask;
    }

    private void TryEmitChatAsEnvelope(string fromPeerNodeId, TransportEnvelope transportEnvelope)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<TransportChatMessage>(transportEnvelope.Payload);
            if (msg == null || !Guid.TryParse(fromPeerNodeId, out var fromGuid)) return;

            var chatPacket = new ChatPacket
            {
                MessageId = Guid.TryParse(msg.MessageId, out var mid) ? mid : Guid.NewGuid(),
                Text = msg.Text ?? string.Empty,
                SentAtUtc = msg.TimestampUtc > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(msg.TimestampUtc)
                    : DateTimeOffset.UtcNow
            };
            var payload = JsonSerializer.SerializeToUtf8Bytes(chatPacket);
            var envelope = new Envelope
            {
                PacketId = Guid.TryParse(transportEnvelope.PacketId, out var pid) ? pid : Guid.NewGuid(),
                MessageId = chatPacket.MessageId,
                SessionId = Guid.Empty,
                OriginNodeId = Guid.TryParse(transportEnvelope.SourceNodeId, out var oid) ? oid : fromGuid,
                CurrentSenderNodeId = fromGuid,
                TargetNodeId = Guid.TryParse(transportEnvelope.DestinationNodeId, out var tid) ? tid : Guid.Empty,
                HopCount = transportEnvelope.HopCount,
                MaxHops = transportEnvelope.MaxHops > 0 ? transportEnvelope.MaxHops : 5,
                PacketType = PacketType.ChatEnvelope,
                Payload = payload
            };
            PacketReceived?.Invoke(envelope, fromGuid);
        }
        catch { }
    }

    public void Dispose() =>
        _connections.EnvelopeReceived -= OnTransportEnvelopeReceived;
}
