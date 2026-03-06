using System.Collections.Concurrent;
using System.Text.Json;
using HexTeam.Messenger.Core.Models;
using HexTeam.Messenger.Core.Transport;
using Microsoft.Extensions.Logging;

namespace HexTeam.Messenger.Core.FileTransfer;

public sealed class FileTransferService
{
    private const int DefaultChunkSize = 32 * 1024;
    private const int MaxChunkAttempts = 3;
    private static readonly TimeSpan ChunkAckTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ThrottleDelay = TimeSpan.FromMilliseconds(10);

    private readonly string _nodeId;
    private readonly PeerConnectionService _connectionService;
    private readonly ILogger<FileTransferService> _logger;
    private readonly ConcurrentDictionary<string, TransportFileTransferInfo> _transfers = new();
    private readonly ConcurrentDictionary<string, FileReceiveContext> _receiving = new();
    private readonly ConcurrentDictionary<string, FileSendContext> _sending = new();
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
        _sending[transfer.TransferId] = new FileSendContext
        {
            TransferId = transfer.TransferId,
            ToPeerNodeId = toPeerNodeId,
            FilePath = filePath
        };

        await SendTransferAsync(transfer, 0, ct);
        return transfer;
    }

    public async Task<TransportFileTransferInfo?> ResumeTransferAsync(string transferId, CancellationToken ct = default)
    {
        if (!_transfers.TryGetValue(transferId, out var transfer) ||
            !_sending.ContainsKey(transferId))
            return null;

        await SendTransferAsync(transfer, transfer.ConfirmedChunks, ct);
        return transfer;
    }

    public void SetReceiveDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            Directory.CreateDirectory(directory);
        _receiveDir = directory;
    }

    private string _receiveDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HexTeamReceived");

    private async Task OnEnvelopeReceived(string fromPeerNodeId, TransportEnvelope envelope)
    {
        switch (envelope.Type)
        {
            case TransportPacketType.FileHeader:
                await HandleFileHeader(fromPeerNodeId, envelope);
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
            case TransportPacketType.FileResumeRequest:
                await HandleFileResumeRequest(envelope);
                break;
        }
    }

    private async Task HandleFileHeader(string fromPeerNodeId, TransportEnvelope envelope)
    {
        var header = JsonSerializer.Deserialize<FileHeaderPacket>(envelope.Payload);
        if (header == null) return;

        if (_receiving.TryGetValue(header.TransferId, out var existing))
        {
            var lastAckedChunkIndex = GetLastContiguousChunkIndex(existing);
            await SendEnvelopeAsync(fromPeerNodeId, TransportPacketType.FileResumeRequest,
                new FileResumeRequestPacket(header.TransferId, lastAckedChunkIndex));
            _logger.LogInformation("Requested resume for transfer {Id} from chunk {Chunk}",
                header.TransferId, lastAckedChunkIndex + 1);
            return;
        }

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

        if (valid && ctx.ReceivedChunks[chunk.ChunkIndex] == null)
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
        var packet = JsonSerializer.Deserialize<FileCompletePacket>(envelope.Payload);
        var transferId = packet?.TransferId;
        if (transferId == null || !_receiving.TryGetValue(transferId, out var ctx)) return;

        if (!HasAllChunks(ctx))
        {
            await SendEnvelopeAsync(ctx.FromPeerNodeId, TransportPacketType.FileResumeRequest,
                new FileResumeRequestPacket(transferId, GetLastContiguousChunkIndex(ctx)));
            _logger.LogWarning("Transfer {Id} incomplete on receiver, requested resume", transferId);
            return;
        }

        _receiving.TryRemove(transferId, out _);

        try
        {
            if (!Directory.Exists(_receiveDir))
                Directory.CreateDirectory(_receiveDir);

            using var fs = File.Create(ctx.SavePath);
            foreach (var chunk in ctx.ReceivedChunks)
            {
                if (chunk != null)
                    await fs.WriteAsync(chunk);
            }

            var savedHash = await FileIntegrityService.ComputeFileHashAsync(ctx.SavePath);
            var hashOk = savedHash == ctx.FileHash;
            if (!hashOk)
                _logger.LogError("File integrity check failed for {Name} (expected={Expected}, got={Got})", ctx.FileName, ctx.FileHash, savedHash);
            else
                _logger.LogInformation("File {Name} saved and verified at {Path}", ctx.FileName, ctx.SavePath);

            if (_transfers.TryGetValue(transferId, out var transfer))
            {
                transfer.State = hashOk ? FileTransferState.Completed : FileTransferState.Failed;
                TransferProgressChanged?.Invoke(transfer);
            }
            FileReceived?.Invoke(transferId, ctx.SavePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save file {Name} to {Path} (receiveDir={Dir})", ctx.FileName, ctx.SavePath, _receiveDir);
            if (_transfers.TryGetValue(transferId, out var transfer))
            {
                transfer.State = FileTransferState.Failed;
                TransferProgressChanged?.Invoke(transfer);
            }
            FileReceived?.Invoke(transferId, $"ERROR: {ex.Message}");
        }
    }

    private async Task HandleFileResumeRequest(TransportEnvelope envelope)
    {
        var resume = JsonSerializer.Deserialize<FileResumeRequestPacket>(envelope.Payload);
        if (resume == null ||
            !_transfers.TryGetValue(resume.TransferId, out var transfer) ||
            !_sending.ContainsKey(resume.TransferId))
            return;

        transfer.ConfirmedChunks = Math.Max(transfer.ConfirmedChunks, resume.LastAckedChunkIndex + 1);
        _logger.LogInformation("Resuming transfer {Id} from chunk {Chunk}",
            resume.TransferId, transfer.ConfirmedChunks);
        await SendTransferAsync(transfer, transfer.ConfirmedChunks);
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

    private async Task SendTransferAsync(TransportFileTransferInfo transfer, int startChunkIndex, CancellationToken ct = default)
    {
        if (!_sending.TryGetValue(transfer.TransferId, out var context))
            throw new InvalidOperationException($"No sender context for transfer {transfer.TransferId}");

        if (transfer.State == FileTransferState.Completed) return;

        await context.SendLock.WaitAsync(ct);
        try
        {
            transfer.State = FileTransferState.Transferring;
            TransferProgressChanged?.Invoke(transfer);

            if (startChunkIndex == 0)
            {
                await SendEnvelopeAsync(context.ToPeerNodeId, TransportPacketType.FileHeader,
                    new FileHeaderPacket(
                        transfer.TransferId,
                        transfer.FileName,
                        transfer.FileSize,
                        transfer.FileHash,
                        transfer.ChunkSize,
                        transfer.TotalChunks), ct);
            }

            using var stream = File.OpenRead(context.FilePath);
            stream.Seek((long)startChunkIndex * transfer.ChunkSize, SeekOrigin.Begin);

            var buffer = new byte[transfer.ChunkSize];
            for (var i = startChunkIndex; i < transfer.TotalChunks && !ct.IsCancellationRequested; i++)
            {
                var acked = await TrySendChunkAsync(stream, context.ToPeerNodeId, transfer, buffer, i, ct);
                if (!acked)
                {
                    transfer.State = FileTransferState.Paused;
                    TransferProgressChanged?.Invoke(transfer);
                    _logger.LogWarning("Transfer {Id} paused on chunk {Chunk}", transfer.TransferId, i);
                    return;
                }

                transfer.ConfirmedChunks = i + 1;
                TransferProgressChanged?.Invoke(transfer);
                await Task.Delay(ThrottleDelay, ct);
            }

            transfer.State = FileTransferState.Completed;
            await SendEnvelopeAsync(context.ToPeerNodeId, TransportPacketType.FileComplete,
                new FileCompletePacket(transfer.TransferId), ct);

            _sending.TryRemove(transfer.TransferId, out _);
            TransferProgressChanged?.Invoke(transfer);
            _logger.LogInformation("File transfer {Id} completed: {Name}", transfer.TransferId, transfer.FileName);
        }
        finally
        {
            context.SendLock.Release();
        }
    }

    private async Task<bool> TrySendChunkAsync(
        Stream stream,
        string toPeerNodeId,
        TransportFileTransferInfo transfer,
        byte[] buffer,
        int chunkIndex,
        CancellationToken ct)
    {
        var read = await stream.ReadAsync(buffer.AsMemory(0, transfer.ChunkSize), ct);
        var chunkData = buffer[..read];
        var chunkHash = FileIntegrityService.ComputeChunkHash(chunkData);
        var chunk = new FileChunkPacket(transfer.TransferId, chunkIndex, chunkHash, chunkData);

        for (var attempt = 0; attempt < MaxChunkAttempts; attempt++)
        {
            var ackKey = $"{transfer.TransferId}_{chunkIndex}";
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _chunkAcks[ackKey] = tcs;

            try
            {
                await SendEnvelopeAsync(toPeerNodeId, TransportPacketType.FileChunk, chunk, ct);
                if (await WaitForAckAsync(tcs, ct))
                    return true;
            }
            finally
            {
                _chunkAcks.TryRemove(ackKey, out _);
            }

            _logger.LogWarning("Chunk {Index} not acked for transfer {Id}, attempt {Attempt}/{Max}",
                chunkIndex, transfer.TransferId, attempt + 1, MaxChunkAttempts);
        }

        return false;
    }

    private static int GetLastContiguousChunkIndex(FileReceiveContext ctx)
    {
        for (var i = 0; i < ctx.ReceivedChunks.Length; i++)
        {
            if (ctx.ReceivedChunks[i] == null)
                return i - 1;
        }

        return ctx.ReceivedChunks.Length - 1;
    }

    private static bool HasAllChunks(FileReceiveContext ctx) =>
        ctx.ReceivedChunks.All(chunk => chunk != null);

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

    private sealed class FileSendContext
    {
        public string TransferId { get; init; } = "";
        public string ToPeerNodeId { get; init; } = "";
        public string FilePath { get; init; } = "";
        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }
}
