using System.Collections.Concurrent;
using System.Text.Json;
using HexTeam.Messenger.Core.Models;
using HexTeam.Messenger.Core.Transport;
using Microsoft.Extensions.Logging;

namespace HexTeam.Messenger.Core.FileTransfer;

public sealed class FileTransferService
{
    private const int DefaultChunkSize = 32 * 1024;
    private static readonly TimeSpan ChunkAckTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ThrottleDelay = TimeSpan.FromMilliseconds(10);

    private readonly string _nodeId;
    private readonly PeerConnectionService _connectionService;
    private readonly ILogger<FileTransferService> _logger;
    private readonly ConcurrentDictionary<string, TransportFileTransferInfo> _transfers = new();
    private readonly ConcurrentDictionary<string, FileReceiveContext> _receiving = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _chunkAcks = new();

    public event Action<TransportFileTransferInfo>? TransferProgressChanged;
    public event Action<string, string>? FileReceived; // transferId, savedPath

    public IReadOnlyDictionary<string, TransportFileTransferInfo> ActiveTransfers => _transfers;

    public FileTransferService(string nodeId, PeerConnectionService connectionService, ILogger<FileTransferService> logger)
    {
        _nodeId = nodeId;
        _connectionService = connectionService;
        _logger = logger;
        _connectionService.EnvelopeReceived += OnEnvelopeReceived;
    }

    public async Task<TransportFileTransferInfo> SendFileAsync(string toPeerNodeId, string filePath, CancellationToken ct = default)
    {
        var fileInfo = new System.IO.FileInfo(filePath);
        var fileHash = await FileIntegrityService.ComputeFileHashAsync(filePath, ct);
        var totalChunks = (int)Math.Ceiling((double)fileInfo.Length / DefaultChunkSize);

        var transfer = new TransportFileTransferInfo
        {
            TransferId = TransportEnvelope.NewPacketId(),
            FileName = fileInfo.Name,
            FileSize = fileInfo.Length,
            FileHash = fileHash,
            ChunkSize = DefaultChunkSize,
            State = FileTransferState.Transferring
        };
        _transfers[transfer.TransferId] = transfer;

        var header = new FileHeaderPacket(transfer.TransferId, transfer.FileName, transfer.FileSize, fileHash, DefaultChunkSize, totalChunks);
        await SendEnvelopeAsync(toPeerNodeId, TransportPacketType.FileHeader, header, ct);

        using var stream = File.OpenRead(filePath);
        var buffer = new byte[DefaultChunkSize];

        for (var i = 0; i < totalChunks && !ct.IsCancellationRequested; i++)
        {
            if (transfer.State == FileTransferState.Paused)
            {
                while (transfer.State == FileTransferState.Paused && !ct.IsCancellationRequested)
                    await Task.Delay(500, ct);
            }

            var read = await stream.ReadAsync(buffer.AsMemory(0, DefaultChunkSize), ct);
            var chunkData = buffer[..read];
            var chunkHash = FileIntegrityService.ComputeChunkHash(chunkData);
            var chunk = new FileChunkPacket(transfer.TransferId, i, chunkHash, chunkData);

            var ackKey = $"{transfer.TransferId}_{i}";
            var tcs = new TaskCompletionSource<bool>();
            _chunkAcks[ackKey] = tcs;

            await SendEnvelopeAsync(toPeerNodeId, TransportPacketType.FileChunk, chunk, ct);

            var acked = await WaitForAckAsync(tcs, ct);
            _chunkAcks.TryRemove(ackKey, out _);

            if (!acked)
            {
                _logger.LogWarning("Chunk {Index} not acked for transfer {Id}, retrying", i, transfer.TransferId);
                i--;
                continue;
            }

            transfer.ConfirmedChunks = i + 1;
            TransferProgressChanged?.Invoke(transfer);
            await Task.Delay(ThrottleDelay, ct);
        }

        transfer.State = FileTransferState.Completed;
        await SendEnvelopeAsync(toPeerNodeId, TransportPacketType.FileComplete,
            new { transfer.TransferId }, ct);

        TransferProgressChanged?.Invoke(transfer);
        _logger.LogInformation("File transfer {Id} completed: {Name}", transfer.TransferId, transfer.FileName);
        return transfer;
    }

    public void SetReceiveDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            Directory.CreateDirectory(directory);
        _receiveDir = directory;
    }

    private string _receiveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HexTeamReceived");

    private async Task OnEnvelopeReceived(string fromPeerNodeId, TransportEnvelope envelope)
    {
        switch (envelope.Type)
        {
            case TransportPacketType.FileHeader:
                HandleFileHeader(fromPeerNodeId, envelope);
                break;
            case TransportPacketType.FileChunk:
                await HandleFileChunk(fromPeerNodeId, envelope);
                break;
            case TransportPacketType.FileChunkAck:
                HandleFileChunkAck(envelope);
                break;
            case TransportPacketType.FileComplete:
                await HandleFileComplete(envelope);
                break;
        }
    }

    private void HandleFileHeader(string fromPeerNodeId, TransportEnvelope envelope)
    {
        var header = JsonSerializer.Deserialize<FileHeaderPacket>(envelope.Payload);
        if (header == null) return;

        if (!Directory.Exists(_receiveDir))
            Directory.CreateDirectory(_receiveDir);

        var ctx = new FileReceiveContext
        {
            TransferId = header.TransferId,
            FileName = header.FileName,
            FileSize = header.FileSize,
            FileHash = header.FileHash,
            TotalChunks = header.TotalChunks,
            ChunkSize = header.ChunkSize,
            FromPeerNodeId = fromPeerNodeId,
            SavePath = Path.Combine(_receiveDir, header.FileName),
            ReceivedChunks = new byte[header.TotalChunks][]
        };
        _receiving[header.TransferId] = ctx;

        var transfer = new TransportFileTransferInfo
        {
            TransferId = header.TransferId,
            FileName = header.FileName,
            FileSize = header.FileSize,
            FileHash = header.FileHash,
            ChunkSize = header.ChunkSize,
            State = FileTransferState.Transferring
        };
        _transfers[header.TransferId] = transfer;
        TransferProgressChanged?.Invoke(transfer);

        _logger.LogInformation("Receiving file {Name} ({Size} bytes) from {From}", header.FileName, header.FileSize, fromPeerNodeId);
    }

    private async Task HandleFileChunk(string fromPeerNodeId, TransportEnvelope envelope)
    {
        var chunk = JsonSerializer.Deserialize<FileChunkPacket>(envelope.Payload);
        if (chunk == null) return;

        if (!_receiving.TryGetValue(chunk.TransferId, out var ctx)) return;

        var valid = FileIntegrityService.VerifyChunkHash(chunk.Data, chunk.ChunkHash);
        var ack = new FileChunkAckPacket(chunk.TransferId, chunk.ChunkIndex, valid);
        await SendEnvelopeAsync(fromPeerNodeId, TransportPacketType.FileChunkAck, ack);

        if (valid)
        {
            ctx.ReceivedChunks[chunk.ChunkIndex] = chunk.Data;
            ctx.ConfirmedCount++;

            if (_transfers.TryGetValue(chunk.TransferId, out var transfer))
            {
                transfer.ConfirmedChunks = ctx.ConfirmedCount;
                TransferProgressChanged?.Invoke(transfer);
            }
        }
        else
        {
            _logger.LogWarning("Invalid chunk {Index} for transfer {Id}", chunk.ChunkIndex, chunk.TransferId);
        }
    }

    private void HandleFileChunkAck(TransportEnvelope envelope)
    {
        var ack = JsonSerializer.Deserialize<FileChunkAckPacket>(envelope.Payload);
        if (ack == null) return;

        var key = $"{ack.TransferId}_{ack.ChunkIndex}";
        if (_chunkAcks.TryGetValue(key, out var tcs))
            tcs.TrySetResult(ack.Success);
    }

    private async Task HandleFileComplete(TransportEnvelope envelope)
    {
        var data = JsonSerializer.Deserialize<JsonElement>(envelope.Payload);
        var transferId = data.GetProperty("transferId").GetString();
        if (transferId == null || !_receiving.TryRemove(transferId, out var ctx)) return;

        try
        {
            using var fs = File.Create(ctx.SavePath);
            foreach (var chunk in ctx.ReceivedChunks)
            {
                if (chunk != null)
                    await fs.WriteAsync(chunk);
            }

            var savedHash = await FileIntegrityService.ComputeFileHashAsync(ctx.SavePath);
            if (savedHash == ctx.FileHash)
            {
                _logger.LogInformation("File {Name} saved and verified at {Path}", ctx.FileName, ctx.SavePath);
                if (_transfers.TryGetValue(transferId, out var transfer))
                {
                    transfer.State = FileTransferState.Completed;
                    TransferProgressChanged?.Invoke(transfer);
                }
                FileReceived?.Invoke(transferId, ctx.SavePath);
            }
            else
            {
                _logger.LogError("File integrity check failed for {Name}", ctx.FileName);
                if (_transfers.TryGetValue(transferId, out var transfer))
                {
                    transfer.State = FileTransferState.Failed;
                    TransferProgressChanged?.Invoke(transfer);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save file {Name}", ctx.FileName);
        }
    }

    private async Task SendEnvelopeAsync<T>(string toPeerNodeId, TransportPacketType type, T payload, CancellationToken ct = default)
    {
        var envelope = new TransportEnvelope
        {
            PacketId = TransportEnvelope.NewPacketId(),
            Type = type,
            SourceNodeId = _nodeId,
            DestinationNodeId = toPeerNodeId,
            Payload = JsonSerializer.SerializeToUtf8Bytes(payload)
        };
        await _connectionService.SendAsync(toPeerNodeId, envelope, ct);
    }

    private static async Task<bool> WaitForAckAsync(TaskCompletionSource<bool> tcs, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ChunkAckTimeout);
        try
        {
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch
        {
            return false;
        }
    }

    private sealed class FileReceiveContext
    {
        public string TransferId { get; init; } = "";
        public string FileName { get; init; } = "";
        public long FileSize { get; init; }
        public string FileHash { get; init; } = "";
        public int TotalChunks { get; init; }
        public int ChunkSize { get; init; }
        public string FromPeerNodeId { get; init; } = "";
        public string SavePath { get; init; } = "";
        public byte[][] ReceivedChunks { get; init; } = [];
        public int ConfirmedCount { get; set; }
    }
}
