using HexTeam.Messenger.Core.Models;

namespace HexTeam.Messenger.Core.FileTransfer;

public sealed class TransportFileTransferInfo
{
    public required string TransferId { get; init; }
    public required string FileName { get; init; }
    public required long FileSize { get; init; }
    public required string FileHash { get; init; }
    public int ChunkSize { get; init; } = 32 * 1024;
    public int TotalChunks => (int)Math.Ceiling((double)FileSize / ChunkSize);
    public int ConfirmedChunks { get; set; }
    public FileTransferState State { get; set; } = FileTransferState.Pending;
    public double Progress => TotalChunks == 0 ? 0 : (double)ConfirmedChunks / TotalChunks;
}
