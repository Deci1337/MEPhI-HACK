using HexTeam.Messenger.Core.Models;
using HexTeam.Messenger.Core.Protocol;
using HexTeam.Messenger.Core.Storage;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace HexTeam.Messenger.Core.Services;

public sealed class PacketRouter
{
    private readonly RelayService _relay;
    private readonly RetryPolicy _retry;
    private readonly MessageSyncService _sync;
    private readonly IMessageStore _messageStore;
    private readonly HandshakeVerifier _verifier;
    private readonly ILogger<PacketRouter> _logger;
    private readonly Guid _localNodeId;

    public event Action<ChatMessage>? ChatMessageReceived;
    public event Action<Guid, AckStatus>? AckReceived;
    public event Action<Envelope>? FilePacketReceived;
    public event Action<Envelope>? VoicePacketReceived;
    public event Action<RelayDecision, Envelope>? PacketRouted;

    public PacketRouter(
        RelayService relay,
        RetryPolicy retry,
        MessageSyncService sync,
        IMessageStore messageStore,
        HandshakeVerifier verifier,
        ILogger<PacketRouter> logger,
        Guid localNodeId)
    {
        _relay = relay;
        _retry = retry;
        _sync = sync;
        _messageStore = messageStore;
        _verifier = verifier;
        _logger = logger;
        _localNodeId = localNodeId;
    }

    public async Task HandleIncomingAsync(Envelope envelope, Guid receivedFromNodeId, CancellationToken ct = default)
    {
        if (!ValidateEnvelope(envelope, receivedFromNodeId))
            return;

        var decision = _relay.ShouldRelay(envelope, receivedFromNodeId);
        PacketRouted?.Invoke(decision, envelope);

        switch (decision)
        {
            case RelayDecision.Deliver:
                await DeliverLocallyAsync(envelope, receivedFromNodeId, ct);
                break;
            case RelayDecision.Forward:
                await _relay.ForwardAsync(envelope, receivedFromNodeId, ct);
                await TryDeliverIfBroadcast(envelope, receivedFromNodeId, ct);
                break;
            case RelayDecision.DropDuplicate:
                _logger.LogDebug("Dropped duplicate packet {PacketId} from {Sender}",
                    envelope.PacketId, receivedFromNodeId);
                break;
            case RelayDecision.DropHopExceeded:
                _logger.LogDebug("Dropped hop-exceeded packet {PacketId}, hops={Hops}/{Max}",
                    envelope.PacketId, envelope.HopCount, envelope.MaxHops);
                break;
        }
    }

    public void TrackForAck(Envelope envelope, Guid targetNodeId)
    {
        if (ProtocolConstants.RequiresAck(envelope.PacketType))
            _retry.Track(envelope, targetNodeId);
    }

    public async Task OnPeerReconnectedAsync(Guid peerNodeId, Guid sessionId, CancellationToken ct = default)
    {
        await _sync.SendInventoryAsync(sessionId, peerNodeId, ct);
        _logger.LogInformation("Sent inventory to reconnected peer {Peer} for session {Session}",
            peerNodeId, sessionId);
    }

    private bool ValidateEnvelope(Envelope envelope, Guid receivedFromNodeId)
    {
        if (envelope.PacketId == Guid.Empty)
        {
            _logger.LogWarning("Rejected packet with empty PacketId from {Sender}", receivedFromNodeId);
            return false;
        }

        if (!_verifier.ValidateOrigin(envelope))
        {
            _logger.LogWarning("Rejected packet {PacketId}: origin validation failed (origin={Origin}, hop={Hop})",
                envelope.PacketId, envelope.OriginNodeId, envelope.HopCount);
            return false;
        }

        if (envelope.Payload == null)
        {
            _logger.LogWarning("Rejected packet {PacketId}: null payload", envelope.PacketId);
            return false;
        }

        if (!Enum.IsDefined(envelope.PacketType))
        {
            _logger.LogWarning("Rejected packet {PacketId}: unknown PacketType {Type}",
                envelope.PacketId, (byte)envelope.PacketType);
            return false;
        }

        return true;
    }

    private async Task DeliverLocallyAsync(Envelope envelope, Guid receivedFromNodeId, CancellationToken ct)
    {
        switch (envelope.PacketType)
        {
            case PacketType.ChatEnvelope:
                HandleChat(envelope);
                break;
            case PacketType.Ack:
                HandleAck(envelope);
                break;
            case PacketType.Inventory:
                await HandleInventoryAsync(envelope, ct);
                break;
            case PacketType.MissingRequest:
                await HandleMissingRequestAsync(envelope, receivedFromNodeId, ct);
                break;
            case PacketType.FileChunk:
            case PacketType.FileChunkAck:
            case PacketType.FileResumeRequest:
                FilePacketReceived?.Invoke(envelope);
                break;
            case PacketType.VoiceStart:
            case PacketType.VoiceFrame:
            case PacketType.VoiceStop:
                VoicePacketReceived?.Invoke(envelope);
                break;
            default:
                _logger.LogDebug("Unhandled packet type {Type} from {Sender}",
                    envelope.PacketType, envelope.OriginNodeId);
                break;
        }
    }

    private async Task TryDeliverIfBroadcast(Envelope envelope, Guid receivedFromNodeId, CancellationToken ct)
    {
        if (envelope.TargetNodeId == Guid.Empty)
            await DeliverLocallyAsync(envelope, receivedFromNodeId, ct);
    }

    private void HandleChat(Envelope envelope)
    {
        try
        {
            var chat = JsonSerializer.Deserialize<ChatPacket>(envelope.Payload);
            if (chat == null) return;

            var msg = new ChatMessage
            {
                MessageId = chat.MessageId,
                SessionId = envelope.SessionId,
                SenderNodeId = envelope.OriginNodeId,
                Text = chat.Text,
                SentAtUtc = chat.SentAtUtc,
                ReceivedAtUtc = DateTimeOffset.UtcNow,
                DeliveryState = MessageDeliveryState.Delivered,
                IsRelayed = envelope.HopCount > 0
            };

            if (_messageStore.Contains(msg.MessageId)) return;
            _messageStore.Add(msg);
            ChatMessageReceived?.Invoke(msg);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Malformed ChatEnvelope payload in packet {PacketId}", envelope.PacketId);
        }
    }

    private void HandleAck(Envelope envelope)
    {
        try
        {
            var ack = JsonSerializer.Deserialize<AckPacket>(envelope.Payload);
            if (ack == null) return;

            _retry.Acknowledge(ack.AckedPacketId);
            AckReceived?.Invoke(ack.AckedPacketId, ack.Status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Malformed Ack payload in packet {PacketId}", envelope.PacketId);
        }
    }

    private async Task HandleInventoryAsync(Envelope envelope, CancellationToken ct)
    {
        try
        {
            var inv = JsonSerializer.Deserialize<InventoryPacket>(envelope.Payload);
            if (inv == null) return;

            var missing = _sync.FindMissing(inv.SessionId, inv.MessageIds);
            if (missing.Count > 0)
                await _sync.RequestMissingAsync(inv.SessionId, missing, envelope.OriginNodeId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Malformed Inventory payload in packet {PacketId}", envelope.PacketId);
        }
    }

    private async Task HandleMissingRequestAsync(Envelope envelope, Guid receivedFromNodeId, CancellationToken ct)
    {
        try
        {
            var req = JsonSerializer.Deserialize<MissingRequestPacket>(envelope.Payload);
            if (req == null) return;

            await _sync.ResendMessagesAsync(envelope.OriginNodeId, req.MissingMessageIds, ct);
            _logger.LogDebug("Resent {Count} requested messages to {Peer}",
                req.MissingMessageIds.Count, envelope.OriginNodeId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Malformed MissingRequest payload in packet {PacketId}", envelope.PacketId);
        }
    }
}
