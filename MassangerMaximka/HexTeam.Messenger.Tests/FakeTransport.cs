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

internal sealed class GatedFakeTransport : ITransport
{
    private readonly Task _gate;
    private readonly List<Guid> _connectedPeers;
    public List<(Envelope Envelope, Guid Target)> Sent { get; } = [];

#pragma warning disable CS0067
    public event Action<Envelope, Guid>? PacketReceived;
#pragma warning restore CS0067

    public GatedFakeTransport(Task gate, params Guid[] connectedPeers)
    {
        _gate = gate;
        _connectedPeers = [..connectedPeers];
    }

    public async Task SendAsync(Envelope envelope, Guid targetNodeId, CancellationToken ct = default)
    {
        await _gate;
        Sent.Add((envelope, targetNodeId));
    }

    public IReadOnlyList<Guid> GetConnectedPeers() => _connectedPeers;
}
