using HexTeam.Messenger.Core.Models;
using HexTeam.Messenger.Core.Protocol;
using HexTeam.Messenger.Core.Storage;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<Guid, Task> _activeSyncs = new();

    public event Action<ChatMessage>? ChatMessageReceived;
    public event Action<Guid, AckStatus>? AckReceived;
    public event Action<Envelope>? FilePacketReceived;
    public event Action<Envelope>? VoicePacketReceived;
    public event Action<RelayDecision, Envelope>? PacketRouted;
    public event Action<Guid, Guid>? PeerReconnected;
    public event Action<Guid>? SyncStarted;
    public event Action<Guid>? SyncCompleted;
    public event Action<Guid, string>? PacketRejected;

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
        {
            _logger.LogDebug("Packet {PacketId} rejected during validation from {Sender}",
                envelope.PacketId, receivedFromNodeId);
            return;
        }

        try
        {
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
                    _logger.LogDebug("Dropped duplicate packet {PacketId} from {Sender} (origin={Origin})",
                        envelope.PacketId, receivedFromNodeId, envelope.OriginNodeId);
                    break;
                case RelayDecision.DropHopExceeded:
                    _logger.LogDebug("Dropped hop-exceeded packet {PacketId}, hops={Hops}/{Max} from {Sender}",
                        envelope.PacketId, envelope.HopCount, envelope.MaxHops, receivedFromNodeId);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing packet {PacketId} from {Sender} (PacketType={Type})",
                envelope.PacketId, receivedFromNodeId, envelope.PacketType);
        }
    }

    public void TrackForAck(Envelope envelope, Guid targetNodeId)
    {
        if (ProtocolConstants.RequiresAck(envelope.PacketType))
            _retry.Track(envelope, targetNodeId);
    }

    public async Task OnPeerConnectedAsync(Guid peerNodeId, Guid? existingSessionId, CancellationToken ct = default)
    {
        if (existingSessionId.HasValue)
        {
            await OnPeerReconnectedAsync(peerNodeId, existingSessionId.Value, ct);
        }
    }

    public async Task OnPeerReconnectedAsync(Guid peerNodeId, Guid sessionId, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_activeSyncs.TryAdd(peerNodeId, tcs.Task))
        {
            _logger.LogDebug("Sync already in progress for peer {Peer}, skipping duplicate", peerNodeId);
            return;
        }

        using var syncTimeoutCts = new CancellationTokenSource(ProtocolConstants.SyncTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, syncTimeoutCts.Token);

        try
        {
            SyncStarted?.Invoke(peerNodeId);
            await _sync.SendInventoryAsync(sessionId, peerNodeId, linkedCts.Token);
            _logger.LogInformation("Sent inventory to reconnected peer {Peer} for session {Session}",
                peerNodeId, sessionId);
            PeerReconnected?.Invoke(peerNodeId, sessionId);
            SyncCompleted?.Invoke(peerNodeId);
            tcs.TrySetResult();
        }
        catch (OperationCanceledException) when (syncTimeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogWarning("Sync with peer {Peer} for session {Session} timed out after {Timeout}s",
                peerNodeId, sessionId, ProtocolConstants.SyncTimeout.TotalSeconds);
            tcs.TrySetResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync with reconnected peer {Peer} for session {Session}",
                peerNodeId, sessionId);
            tcs.TrySetException(ex);
            throw;
        }
        finally
        {
            _activeSyncs.TryRemove(peerNodeId, out _);
        }
    }

    private bool ValidateEnvelope(Envelope envelope, Guid receivedFromNodeId)
    {
        if (envelope.PacketId == Guid.Empty)
        {
            _logger.LogWarning("Rejected packet: empty PacketId from {Sender} (PacketType={Type}, SessionId={Session})",
                receivedFromNodeId, envelope.PacketType, envelope.SessionId);
            PacketRejected?.Invoke(envelope.PacketId, "EmptyPacketId");
            return false;
        }

        if (!_verifier.ValidateOrigin(envelope))
        {
            _logger.LogWarning("Rejected packet {PacketId}: origin validation failed (origin={Origin}, hop={Hop}, sender={Sender})",
                envelope.PacketId, envelope.OriginNodeId, envelope.HopCount, receivedFromNodeId);
            PacketRejected?.Invoke(envelope.PacketId, "OriginValidationFailed");
            return false;
        }

        if (envelope.Payload == null)
        {
            _logger.LogWarning("Rejected packet {PacketId}: null payload from {Sender} (PacketType={Type})",
                envelope.PacketId, receivedFromNodeId, envelope.PacketType);
            PacketRejected?.Invoke(envelope.PacketId, "NullPayload");
            return false;
        }

        if (!Enum.IsDefined(envelope.PacketType))
        {
            _logger.LogWarning("Rejected packet {PacketId}: unknown PacketType {Type} from {Sender}",
                envelope.PacketId, (byte)envelope.PacketType, receivedFromNodeId);
            PacketRejected?.Invoke(envelope.PacketId, "UnknownPacketType");
            return false;
        }

        if (envelope.HopCount < 0)
        {
            _logger.LogWarning("Rejected packet {PacketId}: negative HopCount {Hop} from {Sender}",
                envelope.PacketId, envelope.HopCount, receivedFromNodeId);
            PacketRejected?.Invoke(envelope.PacketId, "NegativeHopCount");
            return false;
        }

        if (envelope.PacketType != PacketType.Ping && envelope.Payload.Length == 0)
        {
            _logger.LogWarning("Rejected packet {PacketId}: empty payload for PacketType={Type} from {Sender}",
                envelope.PacketId, envelope.PacketType, receivedFromNodeId);
            PacketRejected?.Invoke(envelope.PacketId, "EmptyPayload");
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
            if (chat == null)
            {
                _logger.LogWarning("Failed to deserialize ChatPacket from packet {PacketId} (payload length={Length})",
                    envelope.PacketId, envelope.Payload.Length);
                return;
            }

            if (chat.MessageId == Guid.Empty)
            {
                _logger.LogWarning("Rejected ChatPacket with empty MessageId in packet {PacketId}",
                    envelope.PacketId);
                return;
            }

            if (string.IsNullOrEmpty(chat.Text))
            {
                _logger.LogDebug("Received empty text message {MessageId} in packet {PacketId}",
                    chat.MessageId, envelope.PacketId);
            }

            var msg = new ChatMessage
            {
                MessageId = chat.MessageId,
                SessionId = envelope.SessionId,
                SenderNodeId = envelope.OriginNodeId,
                Text = chat.Text ?? string.Empty,
                SentAtUtc = chat.SentAtUtc,
                ReceivedAtUtc = DateTimeOffset.UtcNow,
                DeliveryState = MessageDeliveryState.Delivered,
                IsRelayed = envelope.HopCount > 0
            };

            if (_messageStore.Contains(msg.MessageId))
            {
                _logger.LogDebug("Ignored duplicate message {MessageId} in packet {PacketId}",
                    msg.MessageId, envelope.PacketId);
                return;
            }

            _messageStore.Add(msg);
            ChatMessageReceived?.Invoke(msg);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "JSON deserialization failed for ChatEnvelope packet {PacketId} (payload length={Length})",
                envelope.PacketId, envelope.Payload?.Length ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error handling ChatEnvelope packet {PacketId}",
                envelope.PacketId);
        }
    }

    private void HandleAck(Envelope envelope)
    {
        try
        {
            var ack = JsonSerializer.Deserialize<AckPacket>(envelope.Payload);
            if (ack == null)
            {
                _logger.LogWarning("Failed to deserialize AckPacket from packet {PacketId} (payload length={Length})",
                    envelope.PacketId, envelope.Payload.Length);
                return;
            }

            if (ack.AckedPacketId == Guid.Empty)
            {
                _logger.LogWarning("Rejected AckPacket with empty AckedPacketId in packet {PacketId}",
                    envelope.PacketId);
                return;
            }

            _retry.Acknowledge(ack.AckedPacketId);
            AckReceived?.Invoke(ack.AckedPacketId, ack.Status);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "JSON deserialization failed for AckPacket packet {PacketId} (payload length={Length})",
                envelope.PacketId, envelope.Payload?.Length ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error handling AckPacket packet {PacketId}",
                envelope.PacketId);
        }
    }

    private async Task HandleInventoryAsync(Envelope envelope, CancellationToken ct)
    {
        try
        {
            var inv = JsonSerializer.Deserialize<InventoryPacket>(envelope.Payload);
            if (inv == null)
            {
                _logger.LogWarning("Failed to deserialize InventoryPacket from packet {PacketId} (payload length={Length})",
                    envelope.PacketId, envelope.Payload.Length);
                return;
            }

            if (inv.SessionId == Guid.Empty)
            {
                _logger.LogWarning("Rejected InventoryPacket with empty SessionId in packet {PacketId}",
                    envelope.PacketId);
                return;
            }

            var missing = _sync.FindMissing(inv.SessionId, inv.MessageIds ?? []);
            if (missing.Count > 0)
            {
                _logger.LogDebug("Found {Count} missing messages in session {Session} from peer {Peer}",
                    missing.Count, inv.SessionId, envelope.OriginNodeId);
                await _sync.RequestMissingAsync(inv.SessionId, missing, envelope.OriginNodeId, ct);
            }
            else
            {
                _logger.LogDebug("No missing messages in session {Session} from peer {Peer}",
                    inv.SessionId, envelope.OriginNodeId);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "JSON deserialization failed for InventoryPacket packet {PacketId} (payload length={Length})",
                envelope.PacketId, envelope.Payload?.Length ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error handling InventoryPacket packet {PacketId}",
                envelope.PacketId);
        }
    }

    private async Task HandleMissingRequestAsync(Envelope envelope, Guid receivedFromNodeId, CancellationToken ct)
    {
        try
        {
            var req = JsonSerializer.Deserialize<MissingRequestPacket>(envelope.Payload);
            if (req == null)
            {
                _logger.LogWarning("Failed to deserialize MissingRequestPacket from packet {PacketId} (payload length={Length})",
                    envelope.PacketId, envelope.Payload.Length);
                return;
            }

            if (req.SessionId == Guid.Empty)
            {
                _logger.LogWarning("Rejected MissingRequestPacket with empty SessionId in packet {PacketId}",
                    envelope.PacketId);
                return;
            }

            var missingIds = req.MissingMessageIds ?? [];
            if (missingIds.Count == 0)
            {
                _logger.LogDebug("Received empty MissingRequestPacket in packet {PacketId} from {Peer}",
                    envelope.PacketId, envelope.OriginNodeId);
                return;
            }

            _logger.LogInformation("Resending {Count} requested messages to {Peer} for session {Session}",
                missingIds.Count, envelope.OriginNodeId, req.SessionId);

            await _sync.ResendMessagesAsync(envelope.OriginNodeId, missingIds, ct);

            _logger.LogDebug("Completed resending {Count} messages to {Peer}",
                missingIds.Count, envelope.OriginNodeId);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "JSON deserialization failed for MissingRequestPacket packet {PacketId} (payload length={Length})",
                envelope.PacketId, envelope.Payload?.Length ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error handling MissingRequestPacket packet {PacketId}",
                envelope.PacketId);
        }
    }
}
