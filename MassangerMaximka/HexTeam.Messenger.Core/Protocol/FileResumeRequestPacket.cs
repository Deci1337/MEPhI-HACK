namespace HexTeam.Messenger.Core.Protocol;

public sealed class FileResumeRequestPacket
{
    public Guid TransferId { get; init; }
    public int LastAckedChunkIndex { get; init; }
}
