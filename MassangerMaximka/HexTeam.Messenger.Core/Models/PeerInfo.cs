using System.Net;

namespace HexTeam.Messenger.Core.Models;

public sealed record PeerInfo(
    string NodeId,
    string DisplayName,
    IPEndPoint EndPoint,
    bool IsRelay = false)
{
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    public PeerState State { get; set; } = PeerState.Discovered;
}

public enum PeerState
{
    Discovered,
    Connecting,
    Connected,
    Disconnected
}
