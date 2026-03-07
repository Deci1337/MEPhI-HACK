using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace HexTeam.Messenger.Core.Voice;

public sealed class UdpVoiceTransport : IDisposable
{
    private const int DefaultVoicePort = 45679;
    private const int JitterBufferSize = 10;

    private readonly ILogger<UdpVoiceTransport> _logger;
    private readonly int _listenPort;
    private UdpClient _udpClient;
    private readonly int _actualPort;
    private CancellationTokenSource? _cts;
    private IPEndPoint? _remoteEndPoint;

    private readonly ConcurrentQueue<VoiceFrame> _jitterBuffer = new();
    private int _sequenceNumber;

    public bool IsActive { get; private set; }
    public int ListenPort => _actualPort;
    public VoiceMetrics Metrics { get; } = new();

    // Extra endpoints for channel multicast (in addition to primary _remoteEndPoint)
    private readonly List<IPEndPoint> _extraEndPoints = [];
    public void SetExtraEndPoints(IEnumerable<IPEndPoint> endpoints)
    {
        lock (_extraEndPoints) { _extraEndPoints.Clear(); _extraEndPoints.AddRange(endpoints); }
    }
    public void ClearExtraEndPoints() { lock (_extraEndPoints) _extraEndPoints.Clear(); }

    public event Action<byte[]>? FrameReceived;

    public UdpVoiceTransport(ILogger<UdpVoiceTransport> logger, int listenPort = DefaultVoicePort)
    {
        _logger = logger;
        _listenPort = listenPort;
        _udpClient = BindUdpClient(_listenPort);
        _actualPort = (_udpClient.Client.LocalEndPoint as IPEndPoint)?.Port ?? listenPort;
        _logger.LogInformation("Voice transport pre-bound on :{Port}", _actualPort);
    }

    public void Start(IPEndPoint remoteEndPoint)
    {
        if (IsActive) Stop();

        _remoteEndPoint = remoteEndPoint;
        _cts = new CancellationTokenSource();
        _sequenceNumber = 0;

        if (_udpClient?.Client == null || !_udpClient.Client.IsBound)
        {
            _udpClient = BindUdpClient(_listenPort);
        }
        IsActive = true;
        _ = ReceiveLoopAsync(_cts.Token);
        _logger.LogInformation("Voice transport started on :{Port}, remote={EP}",
            _actualPort, remoteEndPoint);
    }

    public async Task SendFrameAsync(byte[] pcmData)
    {
        if (_udpClient == null || _remoteEndPoint == null || !IsActive) return;

        var frame = new VoiceFrame
        {
            Sequence = Interlocked.Increment(ref _sequenceNumber),
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Data = pcmData
        };

        var packet = SerializeFrame(frame);
        try
        {
            await _udpClient.SendAsync(packet, packet.Length, _remoteEndPoint);
            List<IPEndPoint> extras;
            lock (_extraEndPoints) extras = [.._extraEndPoints];
            foreach (var ep in extras)
                await _udpClient.SendAsync(packet, packet.Length, ep);
            Metrics.FramesSent++;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send voice frame");
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udpClient != null)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync(ct);
                var frame = DeserializeFrame(result.Buffer);
                if (frame == null) continue;

                Metrics.FramesReceived++;
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var latency = now - frame.TimestampMs;
                Metrics.UpdateLatency(latency);

                _jitterBuffer.Enqueue(frame);
                while (_jitterBuffer.Count > JitterBufferSize)
                    _jitterBuffer.TryDequeue(out _);

                while (_jitterBuffer.TryDequeue(out var playFrame))
                    FrameReceived?.Invoke(playFrame.Data);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Metrics.PacketsLost++;
                _logger.LogWarning(ex, "Voice receive error");
            }
        }
    }

    public void Stop()
    {
        IsActive = false;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _remoteEndPoint = null;
        while (_jitterBuffer.TryDequeue(out _)) { }
        _logger.LogInformation("Voice transport stopped (port :{Port} kept bound)", _actualPort);
    }

    private static UdpClient BindUdpClient(int preferredPort)
    {
        for (var offset = 0; offset < 10; offset++)
        {
            try
            {
                var client = new UdpClient(preferredPort + offset);
                return client;
            }
            catch (SocketException) { }
        }
        return new UdpClient(0);
    }

    private static byte[] SerializeFrame(VoiceFrame frame)
    {
        var result = new byte[4 + 8 + frame.Data.Length];
        BitConverter.TryWriteBytes(result.AsSpan(0, 4), frame.Sequence);
        BitConverter.TryWriteBytes(result.AsSpan(4, 8), frame.TimestampMs);
        Buffer.BlockCopy(frame.Data, 0, result, 12, frame.Data.Length);
        return result;
    }

    private static VoiceFrame? DeserializeFrame(byte[] data)
    {
        if (data.Length < 12) return null;
        return new VoiceFrame
        {
            Sequence = BitConverter.ToInt32(data, 0),
            TimestampMs = BitConverter.ToInt64(data, 4),
            Data = data[12..]
        };
    }

    public void Dispose()
    {
        Stop();
        try { _udpClient.Close(); } catch { }
        try { _udpClient.Dispose(); } catch { }
    }
}

public sealed class VoiceFrame
{
    public int Sequence { get; init; }
    public long TimestampMs { get; init; }
    public byte[] Data { get; init; } = [];
}

public sealed class VoiceMetrics
{
    public long FramesSent { get; set; }
    public long FramesReceived { get; set; }
    public long PacketsLost { get; set; }
    public double AvgLatencyMs { get; private set; }
    public double JitterMs { get; private set; }
    public double MaxLatencyMs { get; private set; }

    private double _lastLatency;
    private long _latencyCount;
    private double _latencySum;

    public void UpdateLatency(double latencyMs)
    {
        _latencyCount++;
        _latencySum += latencyMs;
        AvgLatencyMs = _latencySum / _latencyCount;
        JitterMs = Math.Abs(latencyMs - _lastLatency);
        _lastLatency = latencyMs;
        if (latencyMs > MaxLatencyMs) MaxLatencyMs = latencyMs;
    }

    public double PacketLossPercent =>
        FramesSent == 0 ? 0 : (double)PacketsLost / (FramesReceived + PacketsLost) * 100;
}
