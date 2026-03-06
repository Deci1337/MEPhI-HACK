using System.Collections.Concurrent;

namespace HexTeam.Messenger.Core.Security;

/// <summary>
/// Per-peer sliding window rate limiter to protect against spam and flood attacks.
/// Tracks packet count per peer within a configurable time window.
/// </summary>
public sealed class PacketRateLimiter
{
    private readonly int _maxPacketsPerWindow;
    private readonly TimeSpan _window;
    private readonly ConcurrentDictionary<Guid, PeerWindow> _windows = new();

    public PacketRateLimiter(int maxPacketsPerWindow = 100, int windowSeconds = 10)
    {
        _maxPacketsPerWindow = maxPacketsPerWindow;
        _window = TimeSpan.FromSeconds(windowSeconds);
    }

    public RateLimitResult Check(Guid peerNodeId)
    {
        var w = _windows.GetOrAdd(peerNodeId, _ => new PeerWindow());
        w.Prune(DateTimeOffset.UtcNow - _window);
        w.Timestamps.Enqueue(DateTimeOffset.UtcNow);

        if (w.Timestamps.Count > _maxPacketsPerWindow)
        {
            w.ViolationCount++;
            return new RateLimitResult(false, w.Timestamps.Count, w.ViolationCount);
        }

        return new RateLimitResult(true, w.Timestamps.Count, w.ViolationCount);
    }

    public int GetViolationCount(Guid peerNodeId)
        => _windows.TryGetValue(peerNodeId, out var w) ? w.ViolationCount : 0;

    public void Reset(Guid peerNodeId) => _windows.TryRemove(peerNodeId, out _);

    private sealed class PeerWindow
    {
        public ConcurrentQueue<DateTimeOffset> Timestamps { get; } = new();
        public int ViolationCount;

        public void Prune(DateTimeOffset cutoff)
        {
            while (Timestamps.TryPeek(out var ts) && ts < cutoff)
                Timestamps.TryDequeue(out _);
        }
    }
}

public readonly record struct RateLimitResult(bool Allowed, int CurrentCount, int TotalViolations);
