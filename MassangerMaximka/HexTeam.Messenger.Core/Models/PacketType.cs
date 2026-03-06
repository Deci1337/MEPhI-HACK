namespace HexTeam.Messenger.Core.Models;

public enum PacketType : byte
{
    Hello = 1,
    Chat = 2,
    Ack = 3,
    FileHeader = 10,
    FileChunk = 11,
    FileChunkAck = 12,
    FileComplete = 13,
    VoiceFrame = 20,
    Relay = 30,
    Discovery = 40,
    Disconnect = 50,
    Inventory = 60,
    MissingRequest = 61
}
