using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace HexTeam.Messenger.Core.Voice;

public sealed class UdpVoiceTransport : IDisposable
{
    private const int DefaultVoicePort = 45679;
    private const int JitterBufferSize = 10;
    private const int KeepaliveDataSize = 160;

    private readonly ILogger<UdpVoiceTransport> _logger;
    private readonly int _listenPort;
    private UdpClient _udpClient;
    private readonly int _actualPort;
    private CancellationTokenSource? _cts;
    private IPEndPoint? _remoteEndPoint;

    private readonly ConcurrentQueue<VoiceFrame> _jitterBuffer = new();
    private int _sequenceNumber;
    private int _keepaliveTxCount;
    private int _keepaliveRxCount;

    // Observed endpoint: the actual source address of the last peer packet we received.
    // Used as fallback when the announced endpoint doesn't work.
    private volatile IPEndPoint? _observedPeerEndPoint;

    public bool IsActive { get; private set; }
    public int ListenPort => _actualPort;
    public IPEndPoint? RemoteEndPoint => _remoteEndPoint;
    public VoiceMetrics Metrics { get; } = new();

    private readonly List<IPEndPoint> _extraEndPoints = [];
    public void SetExtraEndPoints(IEnumerable<IPEndPoint> endpoints)
    {
        lock (_extraEndPoints) { _extraEndPoints.Clear(); _extraEndPoints.AddRange(endpoints); }
    }
    public void ClearExtraEndPoints() { lock (_extraEndPoints) _extraEndPoints.Clear(); }
    public int ExtraEndPointCount { get { lock (_extraEndPoints) return _extraEndPoints.Count; } }
    public string ExtraEndPointsSummary { get { lock (_extraEndPoints) return string.Join(", ", _extraEndPoints); } }

    // Per-burst send counter, reset by ResetSendDiagnostics() before each PTT burst.
    private long _burstFramesSent;
    private volatile bool _burstFirstLogged;

    public event Action<byte[]>? FrameReceived;

    public UdpVoiceTransport(ILogger<UdpVoiceTransport> logger, int listenPort = DefaultVoicePort)
    {
        _logger = logger;
        _listenPort = listenPort;
        _udpClient = BindUdpClient(_listenPort);
        _actualPort = (_udpClient.Client.LocalEndPoint as IPEndPoint)?.Port ?? listenPort;
        _logger.LogInformation("Voice transport pre-bound on :{Port}", _actualPort);
    }

    public void ResetSendDiagnostics()
    {
        Interlocked.Exchange(ref _burstFramesSent, 0);
        _burstFirstLogged = false;
    }

    public void Start(IPEndPoint remoteEndPoint)
    {
        if (IsActive) Stop();

        _remoteEndPoint = remoteEndPoint;
        _observedPeerEndPoint = null;
        _cts = new CancellationTokenSource();
        _sequenceNumber = 0;

        if (_udpClient?.Client == null || !_udpClient.Client.IsBound)
            _udpClient = BindUdpClient(_listenPort);

        IsActive = true;
        _ = ReceiveLoopAsync(_cts.Token);
        _ = SendKeepAliveAsync(_cts.Token);
        _logger.LogInformation("Voice started on :{Port}, remote={EP}", _actualPort, remoteEndPoint);
    }

    public void StartListening()
    {
        if (IsActive) Stop();
        _remoteEndPoint = null;
        _observedPeerEndPoint = null;
        _cts = new CancellationTokenSource();
        _sequenceNumber = 0;
        if (_udpClient?.Client == null || !_udpClient.Client.IsBound)
            _udpClient = BindUdpClient(_listenPort);
        IsActive = true;
        _ = ReceiveLoopAsync(_cts.Token);
        _ = SendKeepAliveAsync(_cts.Token);
        _logger.LogInformation("Voice listening (channel) on :{Port}", _actualPort);
    }

    public async Task SendFrameAsync(byte[] pcmData)
    {
        if (_udpClient == null || !IsActive) return;

        var frame = new VoiceFrame
        {
            Sequence = Interlocked.Increment(ref _sequenceNumber),
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Data = pcmData
        };

        var packet = SerializeFrame(frame);
        try
        {
            int sendCount = 0;

            if (_remoteEndPoint != null)
            {
                await _udpClient.SendAsync(packet, packet.Length, _remoteEndPoint);
                sendCount++;
            }

            List<IPEndPoint> extras;
            lock (_extraEndPoints) extras = [.._extraEndPoints];
            foreach (var ep in extras)
            {
                await _udpClient.SendAsync(packet, packet.Length, ep);
                sendCount++;
            }

            // Fallback: if no configured endpoints, send to the last observed peer address.
            if (sendCount == 0 && _observedPeerEndPoint != null)
            {
                await _udpClient.SendAsync(packet, packet.Length, _observedPeerEndPoint);
                sendCount++;
                if (!_burstFirstLogged)
                    _logger.LogWarning("Voice TX: using observed fallback endpoint {EP}", _observedPeerEndPoint);
            }

            if (sendCount == 0)
            {
                if (!_burstFirstLogged)
                    _logger.LogError("Voice TX: NO ENDPOINTS - frame dropped! remote=null extras=0 observed=null");
                _burstFirstLogged = true;
                return;
            }

            Metrics.FramesSent++;
            var n = Interlocked.Increment(ref _burstFramesSent);
            if (!_burstFirstLogged)
            {
                _burstFirstLogged = true;
                _logger.LogInformation("Voice TX burst start: {Len}B -> {N} dest(s) remote={R} extras=[{E}] observed={O}",
                    packet.Length, sendCount,
                    _remoteEndPoint?.ToString() ?? "null",
                    string.Join(",", extras.Select(e => e.ToString())),
                    _observedPeerEndPoint?.ToString() ?? "null");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send voice frame");
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        int consecutiveErrors = 0;
        while (!ct.IsCancellationRequested && _udpClient != null)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync(ct);
                consecutiveErrors = 0;

                // Always track the last peer we received from - this is the real reachable address.
                _observedPeerEndPoint = result.RemoteEndPoint;

                var frame = DeserializeFrame(result.Buffer);
                if (frame == null) continue;

                if (frame.Data.Length <= KeepaliveDataSize)
                {
                    var rxCount = Interlocked.Increment(ref _keepaliveRxCount);
                    if (rxCount % 10 == 1)
                        _logger.LogInformation("Voice keepalive RX#{N} from={EP}", rxCount, result.RemoteEndPoint);
                    continue;
                }

                Metrics.FramesReceived++;
                if (Metrics.FramesReceived <= 3)
                    _logger.LogInformation("Voice RX#{N}: {Len}B from={EP}",
                        Metrics.FramesReceived, result.Buffer.Length, result.RemoteEndPoint);

                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var latency = now - frame.TimestampMs;
                Metrics.UpdateLatency(latency);

                _jitterBuffer.Enqueue(frame);
                while (_jitterBuffer.Count > JitterBufferSize)
                    _jitterBuffer.TryDequeue(out _);

                while (_jitterBuffer.TryDequeue(out var playFrame))
                    FrameReceived?.Invoke(playFrame.Data);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException ex)
            {
                consecutiveErrors++;
                Metrics.PacketsLost++;
                _logger.LogWarning(ex, "Voice socket error #{N}", consecutiveErrors);
                if (consecutiveErrors > 50)
                {
                    _logger.LogError("Voice receive loop: too many errors, stopping");
                    break;
                }
                await Task.Delay(10, ct);
            }
            catch (Exception ex)
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
        _logger.LogInformation("Voice stopped (port :{Port} kept bound)", _actualPort);
    }

    private async Task SendKeepAliveAsync(CancellationToken ct)
    {
        for (var burst = 0; burst < 5; burst++)
        {
            try
            {
                var ping = BuildKeepalivePacket();
                await SendPingToAllEndpoints(ping);
                LogKeepaliveSent();
                await Task.Delay(30, ct);
            }
            catch (OperationCanceledException) { return; }
            catch { }
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, ct);
                var ping = BuildKeepalivePacket();
                await SendPingToAllEndpoints(ping);
                LogKeepaliveSent();
            }
            catch (OperationCanceledException) { return; }
            catch { }
        }
    }

    private byte[] BuildKeepalivePacket() => SerializeFrame(new VoiceFrame
    {
        Sequence = 0,
        TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Data = new byte[KeepaliveDataSize]
    });

    private void LogKeepaliveSent()
    {
        var n = Interlocked.Increment(ref _keepaliveTxCount);
        if (n % 10 == 1)
            _logger.LogInformation("Voice keepalive TX#{N} remote={EP} observed={O}",
                n, _remoteEndPoint, _observedPeerEndPoint);
    }

    private async Task SendPingToAllEndpoints(byte[] packet)
    {
        if (_udpClient == null) return;
        int sent = 0;

        if (_remoteEndPoint != null)
        {
            await _udpClient.SendAsync(packet, packet.Length, _remoteEndPoint);
            sent++;
        }

        List<IPEndPoint> extras;
        lock (_extraEndPoints) extras = [.. _extraEndPoints];
        foreach (var ep in extras)
        {
            await _udpClient.SendAsync(packet, packet.Length, ep);
            sent++;
        }

        // Also keepalive to observed endpoint so the NAT pinhole stays open in both directions.
        var observed = _observedPeerEndPoint;
        if (observed != null && !EndPointEquals(observed, _remoteEndPoint) && !extras.Any(e => EndPointEquals(e, observed)))
        {
            await _udpClient.SendAsync(packet, packet.Length, observed);
            sent++;
        }
    }

    private static bool EndPointEquals(IPEndPoint? a, IPEndPoint? b) =>
        a != null && b != null && a.Address.Equals(b.Address) && a.Port == b.Port;

    private static UdpClient BindUdpClient(int preferredPort)
    {
        for (var offset = 0; offset < 10; offset++)
        {
            try
            {
                var client = new UdpClient(new IPEndPoint(IPAddress.Any, preferredPort + offset));
                return client;
            }
            catch (SocketException) { }
        }
        return new UdpClient(new IPEndPoint(IPAddress.Any, 0));
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
