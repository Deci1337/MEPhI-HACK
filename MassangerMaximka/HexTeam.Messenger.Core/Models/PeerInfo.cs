namespace HexTeam.Messenger.Core.Models;

public sealed class PeerInfo
{
    public Guid NodeId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Fingerprint { get; init; } = string.Empty;
    public string IpAddress { get; init; } = string.Empty;
    public int Port { get; init; }
    public DateTimeOffset LastSeen { get; set; }
    public PeerConnectionState State { get; set; } = PeerConnectionState.Discovered;

    public string EndPoint => $"{IpAddress}:{Port}";
}

public enum PeerConnectionState
{
    Discovered,
    Connecting,
    Connected,
    Relayed,
    Degraded,
    Disconnected
}
