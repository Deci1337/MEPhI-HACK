namespace HexTeam.Messenger.Core.Transport;

public sealed record TransportImageMessage(
    string FromNodeId,
    string ToNodeId,
    string FileName,
    string MimeType,
    byte[] Data,
    long TimestampUtc = 0);
