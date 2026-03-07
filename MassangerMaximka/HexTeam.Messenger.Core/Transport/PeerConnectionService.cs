using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace HexTeam.Messenger.Core.Transport;

public sealed class PeerConnectionService : IDisposable
{
    private readonly string _nodeId;
    private readonly int _listenPort;
    private readonly ILogger<PeerConnectionService> _logger;
    private readonly ConcurrentDictionary<string, PeerConnection> _connections = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public event Func<string, TransportEnvelope, Task>? EnvelopeReceived;
    public event Action<string>? PeerConnected;
    public event Action<string>? PeerDisconnected;

    public int ListenPort => _listenPort;
    public IReadOnlyDictionary<string, PeerConnection> Connections => _connections;

    public PeerConnectionService(string nodeId, int listenPort, ILogger<PeerConnectionService> logger)
    {
        _nodeId = nodeId;
        _listenPort = listenPort;
        _logger = logger;
    }

    public void StartListening()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, _listenPort);
        _listener.Start();
        _ = AcceptClientsAsync(_cts.Token);
        _logger.LogInformation("TCP listener started on port {Port}", _listenPort);
    }

    public async Task<bool> ConnectToPeerAsync(string peerNodeId, IPEndPoint endPoint, CancellationToken ct = default)
    {
        if (_connections.ContainsKey(peerNodeId)) return true;
        try
        {
            var client = new TcpClient();
            await client.ConnectAsync(endPoint.Address, endPoint.Port, ct);
            var conn = new PeerConnection(peerNodeId, client);
            _connections[peerNodeId] = conn;

            var hello = new TransportEnvelope
            {
                PacketId = TransportEnvelope.NewPacketId(),
                Type = TransportPacketType.Hello,
                SourceNodeId = _nodeId,
                DestinationNodeId = peerNodeId,
                Payload = System.Text.Encoding.UTF8.GetBytes(_nodeId)
            };
            await EnvelopeSerializer.WriteToStreamAsync(conn.Stream, hello, ct);
            _ = ReceiveLoopAsync(conn, _cts?.Token ?? CancellationToken.None);

            PeerConnected?.Invoke(peerNodeId);
            _logger.LogInformation("Connected to peer {NodeId} at {EP}", peerNodeId, endPoint);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to connect to peer {NodeId} at {EP}", peerNodeId, endPoint);
            return false;
        }
    }

    public async Task SendAsync(string peerNodeId, TransportEnvelope envelope, CancellationToken ct = default)
    {
        if (!_connections.TryGetValue(peerNodeId, out var conn))
            throw new InvalidOperationException($"No connection to peer {peerNodeId}");
        await conn.WriteLock.WaitAsync(ct);
        try
        {
            await EnvelopeSerializer.WriteToStreamAsync(conn.Stream, envelope, ct);
        }
        finally
        {
            conn.WriteLock.Release();
        }
    }

    public async Task BroadcastAsync(TransportEnvelope envelope, CancellationToken ct = default)
    {
        foreach (var conn in _connections.Values)
        {
            await conn.WriteLock.WaitAsync(ct);
            try
            {
                await EnvelopeSerializer.WriteToStreamAsync(conn.Stream, envelope, ct);
            }
            finally
            {
                conn.WriteLock.Release();
            }
        }
    }

    public void DisconnectPeer(string peerNodeId)
    {
        if (_connections.TryRemove(peerNodeId, out var conn))
        {
            conn.Dispose();
            PeerDisconnected?.Invoke(peerNodeId);
            _logger.LogInformation("Disconnected peer {NodeId}", peerNodeId);
        }
    }

    public bool IsConnected(string peerNodeId) =>
        _connections.ContainsKey(peerNodeId);

    public IPEndPoint? GetPeerEndPoint(string peerNodeId)
    {
        if (!_connections.TryGetValue(peerNodeId, out var conn))
            return null;
        return conn.Client.Client.RemoteEndPoint as IPEndPoint;
    }

    private async Task AcceptClientsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                _ = HandleIncomingClientAsync(client, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Error accepting client");
            }
        }
    }

    private async Task HandleIncomingClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            var stream = client.GetStream();
            var hello = await EnvelopeSerializer.ReadFromStreamAsync(stream, ct);
            if (hello == null || hello.Type != TransportPacketType.Hello)
            {
                client.Close();
                return;
            }

            var peerNodeId = hello.SourceNodeId;
            var conn = new PeerConnection(peerNodeId, client);
            _connections[peerNodeId] = conn;

            PeerConnected?.Invoke(peerNodeId);
            _logger.LogInformation("Incoming connection from {NodeId}", peerNodeId);

            await ReceiveLoopAsync(conn, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error handling incoming client");
            client.Close();
        }
    }

    private async Task ReceiveLoopAsync(PeerConnection conn, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && conn.Client.Connected)
            {
                var envelope = await EnvelopeSerializer.ReadFromStreamAsync(conn.Stream, ct);
                if (envelope == null) break;

                var handler = EnvelopeReceived;
                if (handler != null)
                {
                    foreach (var d in handler.GetInvocationList())
                    {
                        try
                        {
                            await ((Func<string, TransportEnvelope, Task>)d)(conn.PeerNodeId, envelope);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Envelope handler failed for {Type} from {Peer}", envelope.Type, conn.PeerNodeId);
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Connection lost with {NodeId}", conn.PeerNodeId);
        }
        finally
        {
            _connections.TryRemove(conn.PeerNodeId, out _);
            conn.Dispose();
            PeerDisconnected?.Invoke(conn.PeerNodeId);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _listener?.Stop();
        foreach (var conn in _connections.Values) conn.Dispose();
        _connections.Clear();
        _cts?.Dispose();
    }
}

public sealed class PeerConnection : IDisposable
{
    public string PeerNodeId { get; }
    public TcpClient Client { get; }
    public NetworkStream Stream { get; }
    public SemaphoreSlim WriteLock { get; } = new(1, 1);
    public DateTime ConnectedAt { get; } = DateTime.UtcNow;

    public PeerConnection(string peerNodeId, TcpClient client)
    {
        PeerNodeId = peerNodeId;
        Client = client;
        Stream = client.GetStream();
    }

    public void Dispose()
    {
        try { Stream.Close(); } catch { }
        try { Client.Close(); } catch { }
        WriteLock.Dispose();
    }
}
