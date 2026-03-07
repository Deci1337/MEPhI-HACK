using HexTeam.Messenger.Core.Transport;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace HexTeam.Messenger.Core.Services;

public sealed class ChannelService
{
    private readonly string _localNodeId;
    private readonly TcpChatTransport _chatTransport;
    private readonly ILogger<ChannelService> _logger;
    private readonly Dictionary<string, ChannelPacket> _channels = new();

    public event Action<ChannelPacket>? InviteReceived;
    public event Action<ChannelPacket>? MembersUpdated;
    public event Action<string, string, bool>? PttStateChanged;

    public string? ActiveChannelId { get; private set; }

    public ChannelService(string localNodeId, TcpChatTransport chatTransport, ILogger<ChannelService> logger)
    {
        _localNodeId = localNodeId;
        _chatTransport = chatTransport;
        _logger = logger;
        _chatTransport.ProtocolPacketReceived += OnProtocolPacketReceived;
    }

    public ChannelPacket CreateChannel(string name)
    {
        var packet = new ChannelPacket
        {
            ChannelId = Guid.NewGuid().ToString("N")[..8],
            ChannelName = string.IsNullOrWhiteSpace(name) ? "Channel" : name,
            FromNodeId = _localNodeId,
            MemberNodeIds = [_localNodeId]
        };

        _channels[packet.ChannelId] = packet;
        ActiveChannelId = packet.ChannelId;
        MembersUpdated?.Invoke(Clone(packet));
        return Clone(packet);
    }

    public async Task SendInvite(string toNodeId, CancellationToken ct = default)
    {
        var channel = GetActiveChannelOrThrow();
        channel.FromNodeId = _localNodeId;
        await _chatTransport.SendPacketAsync(
            toNodeId,
            TransportPacketType.ChannelInvite,
            JsonSerializer.SerializeToUtf8Bytes(channel),
            ct);
    }

    public void HandleInvite(ChannelPacket packet)
    {
        if (string.IsNullOrWhiteSpace(packet.ChannelId)) return;
        _channels[packet.ChannelId] = Clone(packet);
        InviteReceived?.Invoke(Clone(packet));
    }

    public async Task JoinChannel(string channelId, string fromNodeId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fromNodeId))
            throw new InvalidOperationException("Channel join requires inviter node id.");

        if (!_channels.TryGetValue(channelId, out var channel))
        {
            channel = new ChannelPacket
            {
                ChannelId = channelId,
                ChannelName = $"Channel-{channelId}",
                FromNodeId = _localNodeId,
                MemberNodeIds = [_localNodeId]
            };
            _channels[channelId] = channel;
        }

        channel.FromNodeId = _localNodeId;
        if (!channel.MemberNodeIds.Contains(_localNodeId))
            channel.MemberNodeIds.Add(_localNodeId);
        if (!channel.MemberNodeIds.Contains(fromNodeId))
            channel.MemberNodeIds.Add(fromNodeId);
        ActiveChannelId = channelId;

        var payload = JsonSerializer.SerializeToUtf8Bytes(channel);
        await _chatTransport.SendPacketAsync(fromNodeId, TransportPacketType.ChannelJoin, payload, ct);
    }

    public async Task BroadcastMembers(CancellationToken ct = default)
    {
        var channel = GetActiveChannelOrThrow();
        channel.FromNodeId = _localNodeId;
        var payload = JsonSerializer.SerializeToUtf8Bytes(channel);

        foreach (var nodeId in channel.MemberNodeIds.Where(id => id != _localNodeId))
            await _chatTransport.SendPacketAsync(nodeId, TransportPacketType.ChannelMembers, payload, ct);

        MembersUpdated?.Invoke(Clone(channel));
    }

    public async Task LeaveChannel(CancellationToken ct = default)
    {
        if (ActiveChannelId == null || !_channels.TryGetValue(ActiveChannelId, out var channel)) return;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new ChannelPacket
        {
            ChannelId = channel.ChannelId,
            ChannelName = channel.ChannelName,
            FromNodeId = _localNodeId,
            MemberNodeIds = channel.MemberNodeIds.ToList()
        });
        foreach (var nodeId in channel.MemberNodeIds.Where(id => id != _localNodeId))
            await _chatTransport.SendPacketAsync(nodeId, TransportPacketType.ChannelLeave, payload, ct);
        _channels.Remove(ActiveChannelId);
        ActiveChannelId = null;
    }

    public async Task HandleMemberLeave(string nodeId, CancellationToken ct = default)
    {
        var channel = GetActiveChannelOrThrow();
        if (!channel.MemberNodeIds.Remove(nodeId)) return;

        await BroadcastMembers(ct);
    }

    public async Task SetPttState(bool isTalking, CancellationToken ct = default)
    {
        var channel = GetActiveChannelOrThrow();
        var pttPacket = new ChannelPacket
        {
            ChannelId = channel.ChannelId,
            ChannelName = isTalking ? _localNodeId : string.Empty,
            FromNodeId = _localNodeId,
            MemberNodeIds = channel.MemberNodeIds.ToList()
        };
        var payload = JsonSerializer.SerializeToUtf8Bytes(pttPacket);

        foreach (var nodeId in channel.MemberNodeIds.Where(id => id != _localNodeId))
            await _chatTransport.SendPacketAsync(nodeId, TransportPacketType.ChannelPtt, payload, ct);
    }

    private void OnProtocolPacketReceived(string fromNodeId, TransportEnvelope envelope)
    {
        try
        {
            var packet = JsonSerializer.Deserialize<ChannelPacket>(envelope.Payload);
            if (packet == null) return;

            switch (envelope.Type)
            {
                case TransportPacketType.ChannelInvite:
                    HandleInvite(packet);
                    break;
                case TransportPacketType.ChannelJoin:
                    if (!packet.MemberNodeIds.Contains(fromNodeId))
                        packet.MemberNodeIds.Add(fromNodeId);
                    AddOrUpdateMembers(packet);
                    MembersUpdated?.Invoke(Clone(packet));
                    _ = BroadcastMembers();
                    break;
                case TransportPacketType.ChannelLeave:
                    if (_channels.TryGetValue(packet.ChannelId, out var channel))
                    {
                        channel.MemberNodeIds.Remove(fromNodeId);
                        MembersUpdated?.Invoke(Clone(channel));
                    }
                    break;
                case TransportPacketType.ChannelMembers:
                    AddOrUpdateMembers(packet);
                    MembersUpdated?.Invoke(Clone(packet));
                    break;
                case TransportPacketType.ChannelPtt:
                    var isTalking = !string.IsNullOrWhiteSpace(packet.ChannelName);
                    PttStateChanged?.Invoke(packet.ChannelId, fromNodeId, isTalking);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to handle channel packet {Type} from {From}", envelope.Type, fromNodeId);
        }
    }

    private void AddOrUpdateMembers(ChannelPacket packet)
    {
        _channels[packet.ChannelId] = Clone(packet);
        ActiveChannelId ??= packet.ChannelId;
    }

    private ChannelPacket GetActiveChannelOrThrow()
    {
        if (ActiveChannelId == null || !_channels.TryGetValue(ActiveChannelId, out var channel))
            throw new InvalidOperationException("No active channel created or joined.");
        return channel;
    }

    private static ChannelPacket Clone(ChannelPacket packet) =>
        new()
        {
            ChannelId = packet.ChannelId,
            ChannelName = packet.ChannelName,
            FromNodeId = packet.FromNodeId,
            MemberNodeIds = packet.MemberNodeIds.ToList()
        };
}
