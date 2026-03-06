using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace HexTeam.Messenger.Core.Voice;

public sealed class UdpVoiceTransport : IDisposable
{
    private const int FrameSize = 960;
    private const int JitterBufferSize = 5;

    private readonly int _localPort;
    private readonly ILogger<UdpVoiceTransport> _logger;
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    private IPEndPoint? _remoteEndPoint;

    private readonly ConcurrentQueue<VoiceFrame> _jitterBuffer = new();
    private int _sequenceNumber;

    public bool IsActive { get; private set; }
    public int LocalPort => _localPort;
    public VoiceMetrics Metrics { get; } = new();

    public event Action<byte[]>? FrameReceived;

    public UdpVoiceTransport(int localPort, ILogger<UdpVoiceTransport> logger)
    {
        _localPort = localPort;
        _logger = logger;
    }

    public void StartListening()
    {
        if (IsActive) return;
        _cts = new CancellationTokenSource();
        _udpClient = new UdpClient(_localPort);
        IsActive = true;
        _ = ReceiveLoopAsync(_cts.Token);
        _logger.LogInformation("Voice transport listening on port {Port}", _localPort);
    }

    public void Start(IPEndPoint remoteEndPoint)
    {
        _remoteEndPoint = remoteEndPoint;
        StartListening();
        _logger.LogInformation("Voice transport started on port {Port}, remote={EP}", _localPort, remoteEndPoint);
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

                if (_jitterBuffer.Count >= 2 && _jitterBuffer.TryDequeue(out var playFrame))
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
        _udpClient?.Close();
        _udpClient?.Dispose();
        _udpClient = null;
        _logger.LogInformation("Voice transport stopped");
    }

    private static byte[] SerializeFrame(VoiceFrame frame)
    {
        var seqBytes = BitConverter.GetBytes(frame.Sequence);
        var tsBytes = BitConverter.GetBytes(frame.TimestampMs);
        var result = new byte[4 + 8 + frame.Data.Length];
        Buffer.BlockCopy(seqBytes, 0, result, 0, 4);
        Buffer.BlockCopy(tsBytes, 0, result, 4, 8);
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
        _cts?.Dispose();
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
