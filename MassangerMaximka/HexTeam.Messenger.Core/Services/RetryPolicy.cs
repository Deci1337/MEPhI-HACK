using HexTeam.Messenger.Core.Abstractions;
using HexTeam.Messenger.Core.Models;
using HexTeam.Messenger.Core.Protocol;
using HexTeam.Messenger.Core.Storage;
using System.Collections.Concurrent;

namespace HexTeam.Messenger.Core.Services;

public sealed class RetryPolicy : IDisposable
{
    private readonly ITransport _transport;
    private readonly IMessageStore _messageStore;
    private readonly ConcurrentDictionary<Guid, PendingPacket> _pending = new();
    private readonly Timer _timer;

    public RetryPolicy(ITransport transport, IMessageStore messageStore)
    {
        _transport = transport;
        _messageStore = messageStore;
        _timer = new Timer(OnTick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void Track(Envelope envelope, Guid targetNodeId)
    {
        var pending = new PendingPacket(envelope, targetNodeId);
        _pending[envelope.PacketId] = pending;
        _messageStore.UpdateDeliveryState(envelope.MessageId, MessageDeliveryState.Sent);
    }

    public void Acknowledge(Guid packetId)
    {
        if (_pending.TryRemove(packetId, out var pending))
            _messageStore.UpdateDeliveryState(pending.Envelope.MessageId, MessageDeliveryState.Delivered);
    }

    public AckWaitState GetState(Guid packetId) =>
        _pending.TryGetValue(packetId, out var p) ? p.State : AckWaitState.Unknown;

    private void OnTick(object? _)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (id, pending) in _pending)
        {
            if (pending.State != AckWaitState.Waiting) continue;
            if (now - pending.LastAttemptAt < ProtocolConstants.AckTimeout) continue;

            if (pending.RetryCount >= ProtocolConstants.MaxRetryCount)
            {
                pending.State = AckWaitState.Failed;
                _messageStore.UpdateDeliveryState(pending.Envelope.MessageId, MessageDeliveryState.Failed);
                _pending.TryRemove(new KeyValuePair<Guid, PendingPacket>(id, pending));
                continue;
            }

            pending.RetryCount++;
            pending.LastAttemptAt = now;
            _ = _transport.SendAsync(pending.Envelope, pending.TargetNodeId);
        }
    }

    public void Dispose() => _timer.Dispose();
}

public sealed class PendingPacket
{
    public Envelope Envelope { get; }
    public Guid TargetNodeId { get; }
    public int RetryCount { get; set; }
    public DateTimeOffset LastAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public AckWaitState State { get; set; } = AckWaitState.Waiting;

    public PendingPacket(Envelope envelope, Guid targetNodeId)
    {
        Envelope = envelope;
        TargetNodeId = targetNodeId;
    }
}

public enum AckWaitState
{
    Unknown,
    Waiting,
    Acknowledged,
    Failed
}
