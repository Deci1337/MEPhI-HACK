namespace HexTeam.Messenger.Core.FileTransfer;

public sealed record FileHeaderPacket(
    string TransferId,
    string FileName,
    long FileSize,
    string FileHash,
    int ChunkSize,
    int TotalChunks);

public sealed record FileChunkPacket(
    string TransferId,
    int ChunkIndex,
    string ChunkHash,
    byte[] Data);

public sealed record FileChunkAckPacket(
    string TransferId,
    int ChunkIndex,
    bool Success);

public sealed record FileResumeRequestPacket(
    string TransferId,
    int LastAckedChunkIndex);
