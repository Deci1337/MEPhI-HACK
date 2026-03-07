using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HexTeam.Messenger.Core.Transport;

/// <summary>
/// Manages group channel state: create, invite, join, leave, member broadcast.
/// Thread-safe member list protected by _lock.
/// </summary>
public sealed class ChannelService
{
    private readonly string _nodeId;
    private readonly PeerConnectionService _connections;
    private readonly ILogger<ChannelService> _logger;
    private readonly object _lock = new();
    private List<string> _members = [];

    public string? ActiveChannelId   { get; private set; }
    public string? ActiveChannelName { get; private set; }
    public bool IsInChannel => ActiveChannelId != null;

    public IReadOnlyList<string> Members
    {
        get { lock (_lock) return _members.AsReadOnly(); }
    }

    /// <summary>Fired on the calling thread when an invite arrives.</summary>
    public event Action<ChannelPacket>? InviteReceived;

    /// <summary>Fired whenever the member list changes.</summary>
    public event Action<List<string>>? MembersUpdated;

    public ChannelService(string nodeId, PeerConnectionService connections, ILogger<ChannelService> logger)
    {
        _nodeId = nodeId;
        _connections = connections;
        _logger = logger;
        _connections.EnvelopeReceived += OnEnvelopeReceived;
    }

    // --- Public API ---

    public void CreateChannel(string name)
    {
        ActiveChannelId   = Guid.NewGuid().ToString("N")[..8];
        ActiveChannelName = name;
        lock (_lock) _members = [_nodeId];
        _logger.LogInformation("Channel created: {Id} '{Name}'", ActiveChannelId, name);
        FireMembersUpdated();
    }

    public async Task SendInviteAsync(string toNodeId)
    {
        if (ActiveChannelId == null) return;
        var packet = BuildPacket();
        await SendPacketAsync(toNodeId, TransportPacketType.ChannelInvite, packet);
        _logger.LogInformation("Channel invite sent to {NodeId}", toNodeId);
    }

    /// <summary>
    /// Called when WE accept an incoming invite.
    /// Adds self to the received member list and sends ChannelJoin back to inviter.
    /// </summary>
    public async Task JoinChannelAsync(ChannelPacket invite)
    {
        ActiveChannelId   = invite.ChannelId;
        ActiveChannelName = invite.ChannelName;
        lock (_lock)
        {
            _members = [..invite.MemberNodeIds];
            if (!_members.Contains(_nodeId)) _members.Add(_nodeId);
        }
        var packet = BuildPacket();
        await SendPacketAsync(invite.FromNodeId, TransportPacketType.ChannelJoin, packet);
        _logger.LogInformation("Joined channel {Id}", ActiveChannelId);
        FireMembersUpdated();
    }

    public async Task LeaveChannelAsync()
    {
        if (ActiveChannelId == null) return;
        var packet = BuildPacket();
        List<string> snapshot;
        lock (_lock) snapshot = [.._members];
        foreach (var m in snapshot.Where(m => m != _nodeId))
            await SendPacketAsync(m, TransportPacketType.ChannelLeave, packet);

        ActiveChannelId   = null;
        ActiveChannelName = null;
        lock (_lock) _members = [];
        _logger.LogInformation("Left channel");
        FireMembersUpdated();
    }

    public void HandleMemberLeave(string nodeId)
    {
        lock (_lock) _members.Remove(nodeId);
        _logger.LogInformation("Channel member left: {NodeId}", nodeId);
        FireMembersUpdated();
    }

    // --- Internal ---

    private Task OnEnvelopeReceived(string fromPeer, TransportEnvelope envelope)
    {
        switch (envelope.Type)
        {
            case TransportPacketType.ChannelInvite:
                TryDeserialize(envelope, out var inv);
                if (inv != null) InviteReceived?.Invoke(inv);
                break;

            case TransportPacketType.ChannelJoin:
                TryDeserialize(envelope, out var join);
                if (join != null && join.ChannelId == ActiveChannelId)
                {
                    lock (_lock)
                    {
                        foreach (var m in join.MemberNodeIds.Where(m => !_members.Contains(m)))
                            _members.Add(m);
                    }
                    // broadcast updated list to all members
                    _ = BroadcastMembersAsync();
                    FireMembersUpdated();
                }
                break;

            case TransportPacketType.ChannelLeave:
                TryDeserialize(envelope, out var leave);
                if (leave != null) HandleMemberLeave(leave.FromNodeId);
                break;

            case TransportPacketType.ChannelMembers:
                TryDeserialize(envelope, out var members);
                if (members != null && members.ChannelId == ActiveChannelId)
                {
                    lock (_lock) _members = [..members.MemberNodeIds];
                    FireMembersUpdated();
                }
                break;
        }
        return Task.CompletedTask;
    }

    private async Task BroadcastMembersAsync()
    {
        if (ActiveChannelId == null) return;
        var packet = BuildPacket();
        List<string> snapshot;
        lock (_lock) snapshot = [.._members];
        foreach (var m in snapshot.Where(m => m != _nodeId))
            await SendPacketAsync(m, TransportPacketType.ChannelMembers, packet);
    }

    private ChannelPacket BuildPacket()
    {
        lock (_lock)
            return new ChannelPacket
            {
                ChannelId = ActiveChannelId ?? "",
                ChannelName = ActiveChannelName ?? "",
                FromNodeId = _nodeId,
                MemberNodeIds = [.._members]
            };
    }

    private async Task SendPacketAsync(string toNodeId, TransportPacketType type, ChannelPacket packet)
    {
        var envelope = new TransportEnvelope
        {
            PacketId = TransportEnvelope.NewPacketId(),
            Type = type,
            SourceNodeId = _nodeId,
            DestinationNodeId = toNodeId,
            Payload = JsonSerializer.SerializeToUtf8Bytes(packet)
        };
        try { await _connections.SendAsync(toNodeId, envelope); }
        catch (Exception ex) { _logger.LogWarning(ex, "Channel send failed to {NodeId}", toNodeId); }
    }

    private static bool TryDeserialize(TransportEnvelope envelope, out ChannelPacket? packet)
    {
        try
        {
            packet = JsonSerializer.Deserialize<ChannelPacket>(envelope.Payload);
            return packet != null;
        }
        catch { packet = null; return false; }
    }

    private void FireMembersUpdated()
    {
        List<string> snapshot;
        lock (_lock) snapshot = [.._members];
        MembersUpdated?.Invoke(snapshot);
    }
}
