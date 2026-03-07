using HexTeam.Messenger.Core.Transport;

namespace HexTeam.Messenger.Core.Abstractions;

public interface IHexChatService
{
    event Action<string, DeliveryStatus>? DeliveryStatusChanged;
    Task<TransportChatMessage> SendMessageAsync(string toNodeId, string text, CancellationToken ct = default);
}
