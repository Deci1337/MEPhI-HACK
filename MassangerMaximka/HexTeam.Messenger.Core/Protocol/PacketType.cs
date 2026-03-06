namespace HexTeam.Messenger.Core.Protocol;

public enum PacketType : byte
{
    Hello = 1,
    PeerAnnounce = 2,
    SessionOpen = 3,
    ChatEnvelope = 4,
    Ack = 5,
    Inventory = 6,
    MissingRequest = 7,
    FileChunk = 8,
    FileChunkAck = 9,
    FileResumeRequest = 10,
    VoiceStart = 11,
    VoiceFrame = 12,
    VoiceStop = 13,
    Ping = 14,
    QualityReport = 15
}
