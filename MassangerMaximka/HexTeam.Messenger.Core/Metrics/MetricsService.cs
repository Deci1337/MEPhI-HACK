using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using HexTeam.Messenger.Core.Models;
using HexTeam.Messenger.Core.Transport;
using Microsoft.Extensions.Logging;

namespace HexTeam.Messenger.Core.Metrics;

public sealed class MetricsService
{
    private readonly string _nodeId;
    private readonly PeerConnectionService _connectionService;
    private readonly ILogger<MetricsService> _logger;
    private readonly ConcurrentDictionary<string, ConnectionMetrics> _metrics = new();
    private readonly ConcurrentDictionary<string, Stopwatch> _pendingPings = new();

    private CancellationTokenSource? _cts;

    public event Action<ConnectionMetrics>? MetricsUpdated;

    public IReadOnlyDictionary<string, ConnectionMetrics> AllMetrics => _metrics;

    public MetricsService(string nodeId, PeerConnectionService connectionService, ILogger<MetricsService> logger)
    {
        _nodeId = nodeId;
        _connectionService = connectionService;
        _logger = logger;
        _connectionService.EnvelopeReceived += OnEnvelopeReceived;
        _connectionService.PeerConnected += OnPeerConnected;
        _connectionService.PeerDisconnected += OnPeerDisconnected;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = PingLoopAsync(_cts.Token);
        _logger.LogInformation("Metrics service started");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public ConnectionMetrics? GetMetrics(string peerNodeId) =>
        _metrics.GetValueOrDefault(peerNodeId);

    public ConnectionQuality GetQuality(string peerNodeId)
    {
        if (!_metrics.TryGetValue(peerNodeId, out var m))
            return ConnectionQuality.Unknown;

        return m switch
        {
            { RttMs: < 50, PacketLossPercent: < 1 } => ConnectionQuality.Excellent,
            { RttMs: < 150, PacketLossPercent: < 5 } => ConnectionQuality.Good,
            { RttMs: < 500, PacketLossPercent: < 15 } => ConnectionQuality.Fair,
            _ => ConnectionQuality.Poor
        };
    }

    public void RecordRetry(string peerNodeId)
    {
        var m = GetOrCreate(peerNodeId);
        m.RetryCount++;
        m.LastUpdated = DateTime.UtcNow;
        MetricsUpdated?.Invoke(m);
    }

    public void RecordThroughput(string peerNodeId, double bytesPerSec)
    {
        var m = GetOrCreate(peerNodeId);
        m.ThroughputBytesPerSec = bytesPerSec;
        m.LastUpdated = DateTime.UtcNow;
        MetricsUpdated?.Invoke(m);
    }

    public void RecordReconnect(string peerNodeId)
    {
        var m = GetOrCreate(peerNodeId);
        m.ReconnectAttempts++;
        m.LastUpdated = DateTime.UtcNow;
        MetricsUpdated?.Invoke(m);
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var conn in _connectionService.Connections)
            {
                try
                {
                    var pingId = TransportEnvelope.NewPacketId();
                    var sw = Stopwatch.StartNew();
                    _pendingPings[pingId] = sw;

                    var ping = new TransportEnvelope
                    {
                        PacketId = pingId,
                        Type = TransportPacketType.Ack,
                        SourceNodeId = _nodeId,
                        DestinationNodeId = conn.Key,
                        Payload = Encoding.UTF8.GetBytes("ping")
                    };
                    await _connectionService.SendAsync(conn.Key, ping, ct);
                }
                catch { }
            }
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
    }

    private Task OnEnvelopeReceived(string fromPeerNodeId, TransportEnvelope envelope)
    {
        if (envelope.Type == TransportPacketType.Ack &&
            envelope.Payload.Length > 0 &&
            Encoding.UTF8.GetString(envelope.Payload) == "pong")
        {
            if (_pendingPings.TryRemove(envelope.PacketId, out var sw))
            {
                sw.Stop();
                var m = GetOrCreate(fromPeerNodeId);
                m.RttMs = sw.Elapsed.TotalMilliseconds;
                m.LastUpdated = DateTime.UtcNow;
                MetricsUpdated?.Invoke(m);
            }
        }

        if (envelope.Type == TransportPacketType.Ack &&
            envelope.Payload.Length > 0 &&
            Encoding.UTF8.GetString(envelope.Payload) == "ping")
        {
            var pong = new TransportEnvelope
            {
                PacketId = envelope.PacketId,
                Type = TransportPacketType.Ack,
                SourceNodeId = _nodeId,
                DestinationNodeId = fromPeerNodeId,
                Payload = Encoding.UTF8.GetBytes("pong")
            };
            _ = _connectionService.SendAsync(fromPeerNodeId, pong);
        }

        return Task.CompletedTask;
    }

    private void OnPeerConnected(string peerNodeId) => GetOrCreate(peerNodeId);

    private void OnPeerDisconnected(string peerNodeId) =>
        _metrics.TryRemove(peerNodeId, out _);

    private ConnectionMetrics GetOrCreate(string peerNodeId) =>
        _metrics.GetOrAdd(peerNodeId, id => new ConnectionMetrics { PeerNodeId = id });
}

public enum ConnectionQuality
{
    Unknown,
    Excellent,
    Good,
    Fair,
    Poor
}
