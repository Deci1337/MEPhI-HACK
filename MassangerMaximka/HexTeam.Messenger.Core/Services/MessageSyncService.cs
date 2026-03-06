using HexTeam.Messenger.Core.Abstractions;
using HexTeam.Messenger.Core.Models;
using HexTeam.Messenger.Core.Protocol;
using HexTeam.Messenger.Core.Storage;

namespace HexTeam.Messenger.Core.Services;

public sealed class MessageSyncService
{
    private readonly IMessageStore _messageStore;
    private readonly ITransport _transport;
    private readonly Guid _localNodeId;

    public MessageSyncService(IMessageStore messageStore, ITransport transport, Guid localNodeId)
    {
        _messageStore = messageStore;
        _transport = transport;
        _localNodeId = localNodeId;
    }

    public async Task SendInventoryAsync(Guid sessionId, Guid targetNodeId, CancellationToken ct = default)
    {
        var ids = _messageStore.GetMessageIds(sessionId).ToList();
        var packet = new InventoryPacket
        {
            SessionId = sessionId,
            MessageIds = ids,
            SinceUtc = DateTimeOffset.UtcNow.AddMinutes(-30)
        };

        var envelope = BuildEnvelope(PacketType.Inventory, sessionId, targetNodeId,
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(packet));

        await _transport.SendAsync(envelope, targetNodeId, ct);
    }

    public List<Guid> FindMissing(Guid sessionId, IEnumerable<Guid> remoteIds)
    {
        return remoteIds
            .Where(id => !_messageStore.Contains(id))
            .ToList();
    }

    public async Task RequestMissingAsync(Guid sessionId, IEnumerable<Guid> missingIds,
        Guid targetNodeId, CancellationToken ct = default)
    {
        var missing = missingIds.ToList();
        if (missing.Count == 0) return;

        var packet = new MissingRequestPacket
        {
            SessionId = sessionId,
            MissingMessageIds = missing
        };

        var envelope = BuildEnvelope(PacketType.MissingRequest, sessionId, targetNodeId,
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(packet));

        await _transport.SendAsync(envelope, targetNodeId, ct);
    }

    private Envelope BuildEnvelope(PacketType type, Guid sessionId, Guid targetNodeId, byte[] payload)
        => new()
        {
            PacketId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            SessionId = sessionId,
            OriginNodeId = _localNodeId,
            CurrentSenderNodeId = _localNodeId,
            TargetNodeId = targetNodeId,
            PacketType = type,
            Payload = payload
        };
}
