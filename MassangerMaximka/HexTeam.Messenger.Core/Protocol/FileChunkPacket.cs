namespace HexTeam.Messenger.Core.Protocol;

public sealed class FileChunkPacket
{
    public Guid TransferId { get; init; }
    public string FileName { get; init; } = string.Empty;
    public int ChunkIndex { get; init; }
    public int TotalChunks { get; init; }
    public byte[] ChunkData { get; init; } = [];
    public string ChunkHash { get; init; } = string.Empty;
    public string FileHash { get; init; } = string.Empty;
    public long TotalFileSizeBytes { get; init; }
}
