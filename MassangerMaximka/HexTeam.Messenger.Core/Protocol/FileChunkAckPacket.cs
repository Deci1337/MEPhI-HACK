namespace HexTeam.Messenger.Core.Protocol;

public sealed class FileChunkAckPacket
{
    public Guid TransferId { get; init; }
    public int ChunkIndex { get; init; }
    public bool Success { get; init; }
}
