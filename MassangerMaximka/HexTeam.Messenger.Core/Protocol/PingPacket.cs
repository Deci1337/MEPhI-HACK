namespace HexTeam.Messenger.Core.Protocol;

public sealed class PingPacket
{
    public long SentAtUnixMs { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public sealed class QualityReportPacket
{
    public Guid SessionId { get; init; }
    public double RttMs { get; init; }
    public double PacketLossPercent { get; init; }
    public double JitterMs { get; init; }
    public int RetryCount { get; init; }
    public DateTimeOffset ReportedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
