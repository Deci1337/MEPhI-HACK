using System.Net;

namespace HexTeam.Messenger.Core.Models;

public sealed class PeerInfo
{
    public string NodeKey { get; init; } = string.Empty;
    public Guid NodeId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Fingerprint { get; init; } = string.Empty;
    public string IpAddress { get; init; } = string.Empty;
    public int Port { get; init; }
    public DateTimeOffset LastSeen { get; set; }
    public PeerConnectionState State { get; set; } = PeerConnectionState.Discovered;

    public string EndPoint => $"{IpAddress}:{Port}";

    public static PeerInfo FromDiscovery(string nodeId, string displayName, IPEndPoint endPoint, bool isRelay = false)
    {
        var guid = Guid.TryParse(nodeId, out var g) ? g : CreateGuidFromString(nodeId);
        return new PeerInfo
        {
            NodeKey = nodeId,
            NodeId = guid,
            DisplayName = displayName,
            Fingerprint = nodeId.Length >= 8 ? nodeId[..8] : nodeId,
            IpAddress = endPoint.Address.ToString(),
            Port = endPoint.Port
        };
    }

    private static Guid CreateGuidFromString(string s)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return new Guid(bytes.AsSpan(0, 16));
    }
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
