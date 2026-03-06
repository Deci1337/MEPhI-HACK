using HexTeam.Messenger.Core.Protocol;

namespace HexTeam.Messenger.Core.Abstractions;

public interface ITransport
{
    event Action<Envelope, Guid>? PacketReceived;
    Task SendAsync(Envelope envelope, Guid targetNodeId, CancellationToken ct = default);
    IReadOnlyList<Guid> GetConnectedPeers();
}
