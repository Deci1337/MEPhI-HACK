namespace HexTeam.Messenger.Core.Storage;

public interface ISeenPacketStore
{
    bool TryMarkSeen(Guid packetId);
    bool HasSeen(Guid packetId);
    void Prune();
}
