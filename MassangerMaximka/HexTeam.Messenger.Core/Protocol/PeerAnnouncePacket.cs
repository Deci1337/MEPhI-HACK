namespace HexTeam.Messenger.Core.Protocol;

public sealed class PeerAnnouncePacket
{
    public Guid NodeId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Fingerprint { get; init; } = string.Empty;
    public string IpAddress { get; init; } = string.Empty;
    public int Port { get; init; }
}
