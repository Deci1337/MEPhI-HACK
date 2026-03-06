namespace HexTeam.Messenger.Core.Models;

public sealed record DiscoveryBeacon(
    string NodeId,
    string DisplayName,
    int TcpPort,
    bool IsRelay);
