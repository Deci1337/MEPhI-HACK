using HexTeam.Messenger.Core.Models;
using System.Collections.Concurrent;

namespace HexTeam.Messenger.Core.Storage;

public sealed class InMemorySeenPacketStore : ISeenPacketStore
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _seen = new();
    private readonly TimeSpan _ttl = TimeSpan.FromSeconds(ProtocolConstants.SeenPacketCacheDuration);

    public bool TryMarkSeen(Guid packetId)
    {
        return _seen.TryAdd(packetId, DateTimeOffset.UtcNow);
    }

    public bool HasSeen(Guid packetId) => _seen.ContainsKey(packetId);

    public void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow - _ttl;
        foreach (var key in _seen.Keys)
        {
            if (_seen.TryGetValue(key, out var ts) && ts < cutoff)
                _seen.TryRemove(key, out _);
        }
    }
}
