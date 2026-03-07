using HexTeam.Messenger.Core.Abstractions;
using HexTeam.Messenger.Core.Models;
using HexTeam.Messenger.Core.Protocol;
using HexTeam.Messenger.Core.Storage;
using HexTeam.Messenger.Core.Transport;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace HexTeam.Messenger.Core.Services;

public sealed class HexChatService : IHexChatService
{
    private readonly ITransport _transport;
    private readonly PacketRouter _router;
    private readonly NodeIdentity _identity;
    private readonly IMessageStore _messageStore;
    private readonly RetryPolicy _retryPolicy;
    private readonly ILogger<HexChatService> _logger;

    private readonly ConcurrentDictionary<Guid, string> _pendingMessages = new();

    public event Action<string, DeliveryStatus>? DeliveryStatusChanged;

    public HexChatService(
        ITransport transport,
        PacketRouter router,
        NodeIdentity identity,
        IMessageStore messageStore,
        RetryPolicy retryPolicy,
        ILogger<HexChatService> logger)
    {
        _transport = transport;
        _router = router;
        _identity = identity;
        _messageStore = messageStore;
        _retryPolicy = retryPolicy;
        _logger = logger;

        _router.AckReceived += OnAckReceived;
        _retryPolicy.RetryExhausted += OnRetryExhausted;
    }

    public async Task<TransportChatMessage> SendMessageAsync(string toNodeId, string text, CancellationToken ct = default)
    {
        var messageId = Guid.NewGuid();
        var messageIdStr = messageId.ToString();
        var targetGuid = Guid.Parse(toNodeId);
        
        var chatPacket = new ChatPacket
        {
            MessageId = messageId,
            Text = text,
            SentAtUtc = DateTimeOffset.UtcNow
        };

        var payload = JsonSerializer.SerializeToUtf8Bytes(chatPacket);
        var sessionId = _messageStore.GetOrCreateSessionId(toNodeId);

        var envelope = new Envelope
        {
            PacketId = Guid.NewGuid(),
            MessageId = messageId,
            SessionId = sessionId,
            OriginNodeId = _identity.NodeId,
            CurrentSenderNodeId = _identity.NodeId,
            TargetNodeId = targetGuid,
            PacketType = PacketType.ChatEnvelope,
            Payload = payload
        };

        _pendingMessages[envelope.PacketId] = messageIdStr;

        await _transport.SendAsync(envelope, targetGuid, ct);
        _router.TrackForAck(envelope, targetGuid);

        return new TransportChatMessage(
            MessageId: messageIdStr,
            FromNodeId: _identity.NodeId.ToString(),
            ToNodeId: toNodeId,
            Text: text,
            TimestampUtc: chatPacket.SentAtUtc.ToUnixTimeMilliseconds()
        )
        {
            Status = DeliveryStatus.Sent
        };
    }

    private void OnAckReceived(Guid ackedPacketId, AckStatus status)
    {
        if (_pendingMessages.TryRemove(ackedPacketId, out var messageIdStr))
        {
            var deliveryStatus = status == AckStatus.Delivered ? DeliveryStatus.Delivered : DeliveryStatus.Failed;
            DeliveryStatusChanged?.Invoke(messageIdStr, deliveryStatus);
        }
    }

    private void OnRetryExhausted(Guid packetId)
    {
        if (_pendingMessages.TryRemove(packetId, out var messageIdStr))
        {
            DeliveryStatusChanged?.Invoke(messageIdStr, DeliveryStatus.Failed);
        }
    }
}
