namespace HexTeam.Messenger.Core.Models;

public sealed class ConnectionMetrics
{
    public string PeerNodeId { get; init; } = "";
    public double RttMs { get; set; }
    public int RetryCount { get; set; }
    public double ThroughputBytesPerSec { get; set; }
    public double PacketLossPercent { get; set; }
    public double JitterMs { get; set; }
    public int ReconnectAttempts { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
