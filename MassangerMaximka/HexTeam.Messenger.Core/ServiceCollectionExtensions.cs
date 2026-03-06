using HexTeam.Messenger.Core.Discovery;
using HexTeam.Messenger.Core.FileTransfer;
using HexTeam.Messenger.Core.Metrics;
using HexTeam.Messenger.Core.Models;
using HexTeam.Messenger.Core.Transport;
using HexTeam.Messenger.Core.Voice;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HexTeam.Messenger.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddHexMessengerCore(
        this IServiceCollection services,
        NodeConfiguration? config = null)
    {
        config ??= new NodeConfiguration();
        services.AddSingleton(config);

        services.AddSingleton(sp =>
            new UdpDiscoveryService(config.NodeId, config.DisplayName, config.TcpPort, config.DiscoveryPort, config.IsRelay,
                sp.GetRequiredService<ILogger<UdpDiscoveryService>>()));

        services.AddSingleton(sp =>
            new PeerConnectionService(config.NodeId, config.TcpPort,
                sp.GetRequiredService<ILogger<PeerConnectionService>>()));

        services.AddSingleton(sp =>
            new TcpChatTransport(config.NodeId,
                sp.GetRequiredService<PeerConnectionService>(),
                sp.GetRequiredService<ILogger<TcpChatTransport>>()));

        services.AddSingleton(sp =>
            new RelayForwarder(config.NodeId,
                sp.GetRequiredService<PeerConnectionService>(),
                sp.GetRequiredService<ILogger<RelayForwarder>>()));

        services.AddSingleton(sp =>
        {
            var svc = new FileTransferService(config.NodeId,
                sp.GetRequiredService<PeerConnectionService>(),
                sp.GetRequiredService<ILogger<FileTransferService>>());
            svc.SetReceiveDirectory(config.ReceiveDirectory);
            return svc;
        });

        services.AddSingleton(sp =>
            new UdpVoiceTransport(
                sp.GetRequiredService<ILogger<UdpVoiceTransport>>()));

        services.AddSingleton(sp =>
            new MetricsService(config.NodeId,
                sp.GetRequiredService<PeerConnectionService>(),
                sp.GetRequiredService<ILogger<MetricsService>>()));

        return services;
    }
}
