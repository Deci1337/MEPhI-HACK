using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HexTeam.Messenger.Core.Transport;

public sealed class TcpChatTransport
{
    private const int MaxImageSizeBytes = 20 * 1024 * 1024;
    private readonly string _nodeId;
    private readonly PeerConnectionService _connectionService;
    private readonly ILogger<TcpChatTransport> _logger;
    private readonly ConcurrentDictionary<string, TransportChatMessage> _pendingAcks = new();

    public event Action<TransportChatMessage>? MessageReceived;
    public event Action<TransportImageMessage>? ImageReceived;
    public event Action<string, TransportEnvelope>? ProtocolPacketReceived;
    public event Action<string, DeliveryStatus>? DeliveryStatusChanged;

    public TcpChatTransport(string nodeId, PeerConnectionService connectionService, ILogger<TcpChatTransport> logger)
    {
        _nodeId = nodeId;
        _connectionService = connectionService;
        _logger = logger;
        _connectionService.EnvelopeReceived += OnEnvelopeReceived;
    }

    public async Task<TransportChatMessage> SendMessageAsync(string toNodeId, string text, CancellationToken ct = default)
    {
        var msg = new TransportChatMessage(
            TransportEnvelope.NewPacketId(),
            _nodeId,
            toNodeId,
            text,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var envelope = new TransportEnvelope
        {
            PacketId = msg.MessageId,
            Type = TransportPacketType.Chat,
            SourceNodeId = _nodeId,
            DestinationNodeId = toNodeId,
            Payload = JsonSerializer.SerializeToUtf8Bytes(msg)
        };

        _pendingAcks[msg.MessageId] = msg;

        try
        {
            if (_connectionService.IsConnected(toNodeId))
            {
                await _connectionService.SendAsync(toNodeId, envelope, ct);
                msg.Status = DeliveryStatus.Sent;
            }
            else
            {
                await _connectionService.BroadcastAsync(envelope, ct);
                msg.Status = DeliveryStatus.Sent;
            }
            DeliveryStatusChanged?.Invoke(msg.MessageId, msg.Status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send message {Id}", msg.MessageId);
            msg.Status = DeliveryStatus.Failed;
            DeliveryStatusChanged?.Invoke(msg.MessageId, msg.Status);
        }
        return msg;
    }

    public async Task SendImageAsync(string toNodeId, string fileName, byte[] imageBytes, CancellationToken ct = default)
    {
        if (imageBytes.Length > MaxImageSizeBytes)
        {
            _logger.LogWarning("Image {Name} rejected: {Size} bytes exceeds {Max}", fileName, imageBytes.Length, MaxImageSizeBytes);
            return;
        }

        var packet = new ImagePacket
        {
            FileName = string.IsNullOrWhiteSpace(fileName) ? "image.jpg" : fileName,
            Data = imageBytes
        };
        await SendTransportAsync(toNodeId, TransportPacketType.Image, JsonSerializer.SerializeToUtf8Bytes(packet), ct);
    }

    public async Task SendPacketAsync(string toNodeId, TransportPacketType packetType, byte[] payload, CancellationToken ct = default) =>
        await SendTransportAsync(toNodeId, packetType, payload, ct);

    private Task OnEnvelopeReceived(string fromPeerNodeId, TransportEnvelope envelope)
    {
        switch (envelope.Type)
        {
            case TransportPacketType.Chat:
                HandleChatPacket(fromPeerNodeId, envelope);
                break;
            case TransportPacketType.Ack:
                HandleAck(envelope);
                break;
            case TransportPacketType.Image:
                HandleImagePacket(fromPeerNodeId, envelope);
                break;
            case TransportPacketType.ChannelInvite:
            case TransportPacketType.ChannelJoin:
            case TransportPacketType.ChannelLeave:
            case TransportPacketType.ChannelMembers:
            case TransportPacketType.ChannelPtt:
                ProtocolPacketReceived?.Invoke(fromPeerNodeId, envelope);
                break;
        }
        return Task.CompletedTask;
    }

    private void HandleChatPacket(string fromPeerNodeId, TransportEnvelope envelope)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<TransportChatMessage>(envelope.Payload);
            if (msg == null) return;

            var isForMe = msg.ToNodeId == _nodeId
                       || envelope.DestinationNodeId == _nodeId
                       || msg.FromNodeId == fromPeerNodeId;

            if (isForMe)
            {
                _logger.LogInformation("Chat from {From}: {Text}", msg.FromNodeId, msg.Text);
                MessageReceived?.Invoke(msg);
                _ = SendAckAsync(fromPeerNodeId, envelope.PacketId);
            }
            else
            {
                _logger.LogInformation("Chat for {To} (relay by RelayForwarder)", msg.ToNodeId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error handling chat packet");
        }
    }

    private void HandleAck(TransportEnvelope envelope)
    {
        var ackedId = Encoding.UTF8.GetString(envelope.Payload);
        if (_pendingAcks.TryRemove(ackedId, out var msg))
        {
            msg.Status = DeliveryStatus.Delivered;
            DeliveryStatusChanged?.Invoke(ackedId, DeliveryStatus.Delivered);
            _logger.LogInformation("Ack received for message {Id}", ackedId);
        }
    }

    private void HandleImagePacket(string fromPeerNodeId, TransportEnvelope envelope)
    {
        try
        {
            var packet = JsonSerializer.Deserialize<ImagePacket>(envelope.Payload);
            if (packet == null || packet.Data.Length == 0) return;

            if (packet.Data.Length > MaxImageSizeBytes)
            {
                _logger.LogWarning("Dropped oversized image packet from {From}: {Size} bytes", fromPeerNodeId, packet.Data.Length);
                return;
            }

            ImageReceived?.Invoke(new TransportImageMessage(
                fromPeerNodeId,
                envelope.DestinationNodeId,
                packet.FileName,
                packet.MimeType,
                packet.Data,
                envelope.TimestampUtc));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error handling image packet from {From}", fromPeerNodeId);
        }
    }

    /// <summary>
    /// Allows PacketRouter to inject a CoreMessage received through the relay pipeline
    /// into the transport-level MessageReceived stream so UI subscribers observe it.
    /// </summary>
    public void RaiseMessageReceived(TransportChatMessage msg) =>
        MessageReceived?.Invoke(msg);

    private async Task SendAckAsync(string toPeerNodeId, string ackedPacketId)
    {
        var ack = new TransportEnvelope
        {
            PacketId = TransportEnvelope.NewPacketId(),
            Type = TransportPacketType.Ack,
            SourceNodeId = _nodeId,
            DestinationNodeId = toPeerNodeId,
            Payload = Encoding.UTF8.GetBytes(ackedPacketId)
        };
        try
        {
            await _connectionService.SendAsync(toPeerNodeId, ack);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send ack to {NodeId}", toPeerNodeId);
        }
    }

    private async Task SendTransportAsync(string toNodeId, TransportPacketType packetType, byte[] payload, CancellationToken ct)
    {
        var envelope = new TransportEnvelope
        {
            PacketId = TransportEnvelope.NewPacketId(),
            Type = packetType,
            SourceNodeId = _nodeId,
            DestinationNodeId = toNodeId,
            Payload = payload
        };

        if (_connectionService.IsConnected(toNodeId))
            await _connectionService.SendAsync(toNodeId, envelope, ct);
        else
            await _connectionService.BroadcastAsync(envelope, ct);
    }

}
