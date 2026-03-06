namespace HexTeam.Messenger.Core.Models;

public sealed class DiscoveryBeacon
{
    public string NodeId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int TcpPort { get; set; }
    public bool IsRelay { get; set; }

    public DiscoveryBeacon() { }

    public DiscoveryBeacon(string nodeId, string displayName, int tcpPort, bool isRelay)
    {
        NodeId = nodeId;
        DisplayName = displayName;
        TcpPort = tcpPort;
        IsRelay = isRelay;
    }
}
