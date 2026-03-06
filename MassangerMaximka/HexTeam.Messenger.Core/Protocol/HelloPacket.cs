namespace HexTeam.Messenger.Core.Protocol;

public sealed class HelloPacket
{
    public Guid NodeId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Fingerprint { get; init; } = string.Empty;
    public int ListenPort { get; init; }
    public string ProtocolVersion { get; init; } = "1.0";
}
