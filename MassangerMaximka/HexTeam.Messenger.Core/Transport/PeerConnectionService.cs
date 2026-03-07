using HexTeam.Messenger.Core.Security;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace HexTeam.Messenger.Core.Transport;

public sealed class PeerConnectionService : IDisposable
{
    private readonly string _nodeId;
    private readonly int _listenPort;
    private readonly KeyExchangeService _keyExchange;
    private readonly ILogger<PeerConnectionService> _logger;
    private readonly ConcurrentDictionary<string, PeerConnection> _connections = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public event Func<string, TransportEnvelope, Task>? EnvelopeReceived;
    public event Action<string>? PeerConnected;
    public event Action<string>? PeerDisconnected;

    public int ListenPort => _listenPort;
    public IReadOnlyDictionary<string, PeerConnection> Connections => _connections;

    public PeerConnectionService(string nodeId, int listenPort, KeyExchangeService keyExchange, ILogger<PeerConnectionService> logger)
    {
        _nodeId = nodeId;
        _listenPort = listenPort;
        _keyExchange = keyExchange;
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
        var existingNodeId = FindPeerNodeId(endPoint);
        if (!string.IsNullOrEmpty(existingNodeId))
            return true;

        try
        {
            var client = new TcpClient();
            await client.ConnectAsync(endPoint.Address, endPoint.Port, ct);
            var conn = new PeerConnection(peerNodeId, client);
            RegisterConnection(peerNodeId, conn);

            await SendHelloAsync(conn.Stream, peerNodeId, ct);

            var peerHello = await EnvelopeSerializer.ReadFromStreamAsync(conn.Stream, ct);
            if (peerHello?.Type != TransportPacketType.Hello || string.IsNullOrWhiteSpace(peerHello.SourceNodeId))
                throw new InvalidOperationException("Peer did not complete hello handshake.");

            var actualPeerNodeId = peerHello.SourceNodeId;
            if (!string.Equals(actualPeerNodeId, peerNodeId, StringComparison.Ordinal))
            {
                _connections.TryRemove(peerNodeId, out _);
                conn.UpdatePeerNodeId(actualPeerNodeId);
                RegisterConnection(actualPeerNodeId, conn);
            }

            // ECDH key exchange — both sides derive the same AES-256 session key
            await SendKeyExchangeAsync(conn, ct);
            var peerKey = await ReceiveKeyExchangeAsync(conn, ct);
            if (peerKey != null)
                conn.Encryptor = new TrafficEncryptor(_keyExchange.DeriveSharedSecret(peerKey));

            _ = ReceiveLoopAsync(conn, _cts?.Token ?? CancellationToken.None);

            PeerConnected?.Invoke(conn.PeerNodeId);
            _logger.LogInformation("Connected to peer {NodeId} at {EP} (encrypted={Enc})",
                conn.PeerNodeId, endPoint, conn.Encryptor != null);
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
            await WriteEnvelopeAsync(conn, envelope, ct);
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
                await WriteEnvelopeAsync(conn, envelope, ct);
            }
            finally
            {
                conn.WriteLock.Release();
            }
        }
    }

    private static async Task WriteEnvelopeAsync(PeerConnection conn, TransportEnvelope envelope, CancellationToken ct)
    {
        if (conn.Encryptor != null)
        {
            var json = EnvelopeSerializer.SerializeToJson(envelope);
            await TrafficEncryptor.WriteEncryptedFrameAsync(conn.Stream, json, conn.Encryptor, ct);
        }
        else
        {
            await EnvelopeSerializer.WriteToStreamAsync(conn.Stream, envelope, ct);
        }
    }

    private static async Task<TransportEnvelope?> ReadEnvelopeAsync(PeerConnection conn, CancellationToken ct)
    {
        if (conn.Encryptor != null)
        {
            var plaintext = await TrafficEncryptor.ReadEncryptedFrameAsync(conn.Stream, conn.Encryptor, ct);
            return plaintext == null ? null : EnvelopeSerializer.DeserializeFromJson(plaintext);
        }
        return await EnvelopeSerializer.ReadFromStreamAsync(conn.Stream, ct);
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

    public string? FindPeerNodeId(IPEndPoint endPoint)
    {
        foreach (var kvp in _connections)
        {
            if (kvp.Value.Client.Client.RemoteEndPoint is not IPEndPoint remote)
                continue;

            if (remote.Address.Equals(endPoint.Address) && remote.Port == endPoint.Port)
                return kvp.Key;
        }

        return null;
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
            RegisterConnection(peerNodeId, conn);

            await SendHelloAsync(conn.Stream, peerNodeId, ct);

            // ECDH key exchange — receive peer key first, then send ours
            var peerKey = await ReceiveKeyExchangeAsync(conn, ct);
            await SendKeyExchangeAsync(conn, ct);
            if (peerKey != null)
                conn.Encryptor = new TrafficEncryptor(_keyExchange.DeriveSharedSecret(peerKey));

            PeerConnected?.Invoke(peerNodeId);
            _logger.LogInformation("Incoming connection from {NodeId} (encrypted={Enc})",
                peerNodeId, conn.Encryptor != null);

            await ReceiveLoopAsync(conn, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error handling incoming client");
            client.Close();
        }
    }

    private async Task SendKeyExchangeAsync(PeerConnection conn, CancellationToken ct)
    {
        var packet = new TransportEnvelope
        {
            PacketId = TransportEnvelope.NewPacketId(),
            Type = TransportPacketType.KeyExchange,
            SourceNodeId = _nodeId,
            DestinationNodeId = conn.PeerNodeId,
            Payload = _keyExchange.PublicKey
        };
        await EnvelopeSerializer.WriteToStreamAsync(conn.Stream, packet, ct);
    }

    private Task SendHelloAsync(NetworkStream stream, string peerNodeId, CancellationToken ct)
    {
        var hello = new TransportEnvelope
        {
            PacketId = TransportEnvelope.NewPacketId(),
            Type = TransportPacketType.Hello,
            SourceNodeId = _nodeId,
            DestinationNodeId = peerNodeId,
            Payload = System.Text.Encoding.UTF8.GetBytes(_nodeId)
        };

        return EnvelopeSerializer.WriteToStreamAsync(stream, hello, ct);
    }

    private async Task<byte[]?> ReceiveKeyExchangeAsync(PeerConnection conn, CancellationToken ct)
    {
        var packet = await EnvelopeSerializer.ReadFromStreamAsync(conn.Stream, ct);
        if (packet?.Type != TransportPacketType.KeyExchange)
        {
            _logger.LogWarning("Expected KeyExchange from {NodeId}, got {Type}", conn.PeerNodeId, packet?.Type);
            return null;
        }
        return packet.Payload;
    }

    private async Task ReceiveLoopAsync(PeerConnection conn, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && conn.Client.Connected)
            {
                var envelope = await ReadEnvelopeAsync(conn, ct);
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

    private void RegisterConnection(string peerNodeId, PeerConnection conn)
    {
        if (_connections.TryGetValue(peerNodeId, out var existing) && !ReferenceEquals(existing, conn))
            existing.Dispose();

        _connections[peerNodeId] = conn;
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
    public string PeerNodeId { get; private set; }
    public TcpClient Client { get; }
    public NetworkStream Stream { get; }
    public SemaphoreSlim WriteLock { get; } = new(1, 1);
    public DateTime ConnectedAt { get; } = DateTime.UtcNow;

    /// <summary>Per-peer AES-256-GCM encryptor derived from ECDH handshake. Null until key exchange completes.</summary>
    public TrafficEncryptor? Encryptor { get; set; }

    public PeerConnection(string peerNodeId, TcpClient client)
    {
        PeerNodeId = peerNodeId;
        Client = client;
        Stream = client.GetStream();
    }

    public void UpdatePeerNodeId(string peerNodeId) => PeerNodeId = peerNodeId;

    public void Dispose()
    {
        try { Stream.Close(); } catch { }
        try { Client.Close(); } catch { }
        WriteLock.Dispose();
    }
}
