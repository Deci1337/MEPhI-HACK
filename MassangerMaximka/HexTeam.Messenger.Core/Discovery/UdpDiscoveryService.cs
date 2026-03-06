using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using HexTeam.Messenger.Core.Models;
using Microsoft.Extensions.Logging;

namespace HexTeam.Messenger.Core.Discovery;

public sealed class UdpDiscoveryService : IDisposable
{
    private const int DiscoveryPort = 45678;
    private static readonly TimeSpan BeaconInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(10);

    private readonly string _nodeId;
    private readonly string _displayName;
    private readonly int _tcpPort;
    private readonly bool _isRelay;
    private readonly ILogger<UdpDiscoveryService> _logger;
    private readonly ConcurrentDictionary<string, PeerInfo> _peers = new();

    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;

    public event Action<PeerInfo>? PeerDiscovered;
    public event Action<string>? PeerLost;

    public IReadOnlyDictionary<string, PeerInfo> Peers => _peers;

    public UdpDiscoveryService(string nodeId, string displayName, int tcpPort, bool isRelay, ILogger<UdpDiscoveryService> logger)
    {
        _nodeId = nodeId;
        _displayName = displayName;
        _tcpPort = tcpPort;
        _isRelay = isRelay;
        _logger = logger;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _udpClient = new UdpClient();
        _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        _udpClient.EnableBroadcast = true;

        _ = SendBeaconsAsync(_cts.Token);
        _ = ReceiveBeaconsAsync(_cts.Token);
        _ = PruneStaleAsync(_cts.Token);

        _logger.LogInformation("Discovery started on port {Port}, nodeId={NodeId}", DiscoveryPort, _nodeId);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _udpClient?.Close();
        _udpClient?.Dispose();
        _udpClient = null;
        _logger.LogInformation("Discovery stopped");
    }

    public PeerInfo AddManualPeer(string nodeId, string displayName, IPEndPoint endPoint, bool isRelay = false)
    {
        var peer = new PeerInfo(nodeId, displayName, endPoint, isRelay)
        {
            LastSeen = DateTime.UtcNow,
            State = PeerState.Discovered
        };
        _peers[nodeId] = peer;
        PeerDiscovered?.Invoke(peer);
        _logger.LogInformation("Manual peer added: {NodeId} at {EndPoint}", nodeId, endPoint);
        return peer;
    }

    private async Task SendBeaconsAsync(CancellationToken ct)
    {
        var beacon = new DiscoveryBeacon(_nodeId, _displayName, _tcpPort, _isRelay);
        var data = JsonSerializer.SerializeToUtf8Bytes(beacon);
        var broadcastEp = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_udpClient != null)
                    await _udpClient.SendAsync(data, data.Length, broadcastEp);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to send beacon");
            }
            await Task.Delay(BeaconInterval, ct).ConfigureAwait(false);
        }
    }

    private async Task ReceiveBeaconsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_udpClient == null) break;
                var result = await _udpClient.ReceiveAsync(ct);
                var beacon = JsonSerializer.Deserialize<DiscoveryBeacon>(result.Buffer);
                if (beacon == null || beacon.NodeId == _nodeId) continue;

                var ep = new IPEndPoint(result.RemoteEndPoint.Address, beacon.TcpPort);
                var isNew = !_peers.ContainsKey(beacon.NodeId);
                var peer = new PeerInfo(beacon.NodeId, beacon.DisplayName, ep, beacon.IsRelay)
                {
                    LastSeen = DateTime.UtcNow
                };
                _peers[beacon.NodeId] = peer;

                if (isNew)
                {
                    _logger.LogInformation("Peer discovered: {NodeId} ({Name}) at {EP}", beacon.NodeId, beacon.DisplayName, ep);
                    PeerDiscovered?.Invoke(peer);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Error receiving beacon");
            }
        }
    }

    private async Task PruneStaleAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            var cutoff = DateTime.UtcNow - PeerTimeout;
            foreach (var kvp in _peers)
            {
                if (kvp.Value.LastSeen < cutoff)
                {
                    _peers.TryRemove(kvp.Key, out _);
                    PeerLost?.Invoke(kvp.Key);
                    _logger.LogInformation("Peer lost: {NodeId}", kvp.Key);
                }
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
