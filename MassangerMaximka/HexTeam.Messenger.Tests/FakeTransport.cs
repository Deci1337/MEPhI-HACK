using HexTeam.Messenger.Core.Abstractions;
using HexTeam.Messenger.Core.Protocol;

namespace HexTeam.Messenger.Tests;

internal sealed class FakeTransport : ITransport
{
    private readonly List<Guid> _connectedPeers;
    public List<(Envelope Envelope, Guid Target)> Sent { get; } = [];

    public event Action<Envelope, Guid>? PacketReceived;

    public FakeTransport(params Guid[] connectedPeers)
    {
        _connectedPeers = [..connectedPeers];
    }

    public Task SendAsync(Envelope envelope, Guid targetNodeId, CancellationToken ct = default)
    {
        Sent.Add((envelope, targetNodeId));
        return Task.CompletedTask;
    }

    public IReadOnlyList<Guid> GetConnectedPeers() => _connectedPeers;

    public void SimulateReceive(Envelope envelope, Guid fromNodeId)
        => PacketReceived?.Invoke(envelope, fromNodeId);
}
