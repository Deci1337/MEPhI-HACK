namespace HexTeam.Messenger.Core.Protocol;

public sealed class VoiceStartPacket
{
    public Guid SessionId { get; init; }
    public string Codec { get; init; } = "OPUS";
    public int SampleRate { get; init; } = 48000;
    public int Channels { get; init; } = 1;
}

public sealed class VoiceFramePacket
{
    public Guid SessionId { get; init; }
    public uint SequenceNumber { get; init; }
    public uint Timestamp { get; init; }
    public byte[] FrameData { get; init; } = [];
}

public sealed class VoiceStopPacket
{
    public Guid SessionId { get; init; }
}
