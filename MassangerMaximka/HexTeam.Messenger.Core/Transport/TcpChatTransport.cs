using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using HexTeam.Messenger.Core.Models;
using Microsoft.Extensions.Logging;

namespace HexTeam.Messenger.Core.Transport;

public sealed class TcpChatTransport
{
    private readonly string _nodeId;
    private readonly PeerConnectionService _connectionService;
    private readonly ILogger<TcpChatTransport> _logger;
    private readonly ConcurrentDictionary<string, ChatMessage> _pendingAcks = new();

    public event Action<ChatMessage>? MessageReceived;
    public event Action<string, DeliveryStatus>? DeliveryStatusChanged;

    public TcpChatTransport(string nodeId, PeerConnectionService connectionService, ILogger<TcpChatTransport> logger)
    {
        _nodeId = nodeId;
        _connectionService = connectionService;
        _logger = logger;
        _connectionService.EnvelopeReceived += OnEnvelopeReceived;
    }

    public async Task<ChatMessage> SendMessageAsync(string toNodeId, string text, CancellationToken ct = default)
    {
        var msg = new ChatMessage(
            Envelope.NewPacketId(),
            _nodeId,
            toNodeId,
            text,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var envelope = new Envelope
        {
            PacketId = msg.MessageId,
            Type = PacketType.Chat,
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

    private Task OnEnvelopeReceived(string fromPeerNodeId, Envelope envelope)
    {
        switch (envelope.Type)
        {
            case PacketType.Chat:
                HandleChatPacket(fromPeerNodeId, envelope);
                break;
            case PacketType.Ack:
                HandleAck(envelope);
                break;
        }
        return Task.CompletedTask;
    }

    private void HandleChatPacket(string fromPeerNodeId, Envelope envelope)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<ChatMessage>(envelope.Payload);
            if (msg == null) return;

            if (msg.ToNodeId == _nodeId)
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

    private void HandleAck(Envelope envelope)
    {
        var ackedId = Encoding.UTF8.GetString(envelope.Payload);
        if (_pendingAcks.TryRemove(ackedId, out var msg))
        {
            msg.Status = DeliveryStatus.Delivered;
            DeliveryStatusChanged?.Invoke(ackedId, DeliveryStatus.Delivered);
            _logger.LogInformation("Ack received for message {Id}", ackedId);
        }
    }

    private async Task SendAckAsync(string toPeerNodeId, string ackedPacketId)
    {
        var ack = new Envelope
        {
            PacketId = Envelope.NewPacketId(),
            Type = PacketType.Ack,
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

}
