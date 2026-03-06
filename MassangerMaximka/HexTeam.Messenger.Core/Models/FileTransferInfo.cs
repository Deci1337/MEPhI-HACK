namespace HexTeam.Messenger.Core.Models;

public sealed class FileTransferInfo
{
    public Guid TransferId { get; init; } = Guid.NewGuid();
    public Guid SessionId { get; init; }
    public Guid SenderNodeId { get; init; }
    public Guid ReceiverNodeId { get; init; }
    public string FileName { get; init; } = string.Empty;
    public long TotalSizeBytes { get; init; }
    public int TotalChunks { get; init; }
    public int AckedChunks { get; set; }
    public string FileHash { get; init; } = string.Empty;
    public FileTransferState State { get; set; } = FileTransferState.Pending;
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; set; }

    public double ProgressPercent =>
        TotalChunks == 0 ? 0 : (double)AckedChunks / TotalChunks * 100.0;
}

public enum FileTransferState
{
    Pending,
    InProgress,
    Transferring = InProgress,
    Paused,
    Completed,
    Failed,
    IntegrityError
}
