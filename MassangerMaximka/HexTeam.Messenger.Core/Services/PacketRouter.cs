using HexTeam.Messenger.Core.Models;
using HexTeam.Messenger.Core.Protocol;
using HexTeam.Messenger.Core.Storage;
using System.Text.Json;

namespace HexTeam.Messenger.Core.Services;

public sealed class PacketRouter
{
    private readonly RelayService _relay;
    private readonly RetryPolicy _retry;
    private readonly MessageSyncService _sync;
    private readonly IMessageStore _messageStore;
    private readonly Guid _localNodeId;

    public event Action<ChatMessage>? ChatMessageReceived;
    public event Action<Guid, AckStatus>? AckReceived;
    public event Action<Guid, IReadOnlyList<Guid>>? MissingMessagesRequested;
    public event Action<Envelope>? FilePacketReceived;
    public event Action<Envelope>? VoicePacketReceived;
    public event Action<RelayDecision, Envelope>? PacketRouted;

    public PacketRouter(
        RelayService relay,
        RetryPolicy retry,
        MessageSyncService sync,
        IMessageStore messageStore,
        Guid localNodeId)
    {
        _relay = relay;
        _retry = retry;
        _sync = sync;
        _messageStore = messageStore;
        _localNodeId = localNodeId;
    }

    public async Task HandleIncomingAsync(Envelope envelope, Guid receivedFromNodeId, CancellationToken ct = default)
    {
        var decision = _relay.ShouldRelay(envelope, receivedFromNodeId);
        PacketRouted?.Invoke(decision, envelope);

        switch (decision)
        {
            case RelayDecision.Deliver:
                await DeliverLocallyAsync(envelope, ct);
                break;
            case RelayDecision.Forward:
                await _relay.ForwardAsync(envelope, receivedFromNodeId, ct);
                await TryDeliverIfBroadcast(envelope, ct);
                break;
            case RelayDecision.DropDuplicate:
            case RelayDecision.DropHopExceeded:
                break;
        }
    }

    private async Task DeliverLocallyAsync(Envelope envelope, CancellationToken ct)
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
                HandleMissingRequest(envelope);
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
        }
    }

    private async Task TryDeliverIfBroadcast(Envelope envelope, CancellationToken ct)
    {
        if (envelope.TargetNodeId == Guid.Empty)
            await DeliverLocallyAsync(envelope, ct);
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
        catch { /* malformed packet */ }
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
        catch { /* malformed packet */ }
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
        catch { /* malformed packet */ }
    }

    private void HandleMissingRequest(Envelope envelope)
    {
        try
        {
            var req = JsonSerializer.Deserialize<MissingRequestPacket>(envelope.Payload);
            if (req == null) return;

            MissingMessagesRequested?.Invoke(envelope.OriginNodeId, req.MissingMessageIds);
        }
        catch { /* malformed packet */ }
    }
}
