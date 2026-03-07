using HexTeam.Messenger.Core.Abstractions;
using HexTeam.Messenger.Core.Discovery;
using HexTeam.Messenger.Core.FileTransfer;
using HexTeam.Messenger.Core.Metrics;
using HexTeam.Messenger.Core.Models;
using HexTeam.Messenger.Core.Security;
using HexTeam.Messenger.Core.Services;
using HexTeam.Messenger.Core.Storage;
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

        var nodeGuid = Guid.TryParse(config.NodeId, out var g) ? g : Guid.NewGuid();
        var identity = new NodeIdentity(nodeGuid, config.DisplayName);
        services.AddSingleton(identity);

        services.AddSingleton<ISeenPacketStore, InMemorySeenPacketStore>();
        services.AddSingleton<IMessageStore, InMemoryMessageStore>();

        services.AddSingleton(sp =>
            new HandshakeVerifier(sp.GetRequiredService<NodeIdentity>()));

        services.AddSingleton<KeyExchangeService>();
        services.AddSingleton<E2EEncryptionService>();
        services.AddSingleton<PacketRateLimiter>();

        // PeerConnectionService must be created before TransportAdapter
        services.AddSingleton(sp =>
            new PeerConnectionService(config.NodeId, config.TcpPort,
                sp.GetRequiredService<KeyExchangeService>(),
                sp.GetRequiredService<ILogger<PeerConnectionService>>()));

        // TransportAdapter bridges ITransport (Core Envelope) to PeerConnectionService (TransportEnvelope)
        services.AddSingleton<ITransport>(sp =>
            new TransportAdapter(sp.GetRequiredService<PeerConnectionService>()));

        services.AddSingleton(sp =>
            new RelayService(
                sp.GetRequiredService<ISeenPacketStore>(),
                sp.GetRequiredService<ITransport>(),
                nodeGuid));

        services.AddSingleton(sp =>
            new RetryPolicy(
                sp.GetRequiredService<ITransport>(),
                sp.GetRequiredService<IMessageStore>()));

        services.AddSingleton(sp =>
            new MessageSyncService(
                sp.GetRequiredService<IMessageStore>(),
                sp.GetRequiredService<ITransport>(),
                nodeGuid));

        services.AddSingleton(sp =>
        {
            var router = new PacketRouter(
                sp.GetRequiredService<RelayService>(),
                sp.GetRequiredService<RetryPolicy>(),
                sp.GetRequiredService<MessageSyncService>(),
                sp.GetRequiredService<IMessageStore>(),
                sp.GetRequiredService<HandshakeVerifier>(),
                sp.GetRequiredService<ILogger<PacketRouter>>(),
                nodeGuid);

            // Task 2: route incoming Core Envelopes through PacketRouter
            sp.GetRequiredService<ITransport>().PacketReceived +=
                (envelope, fromNodeId) => _ = router.HandleIncomingAsync(envelope, fromNodeId);

            // Task 2: bridge PacketRouter chat events back to TcpChatTransport for UI consumers
            router.ChatMessageReceived += msg =>
            {
                var transport = sp.GetRequiredService<TcpChatTransport>();
                transport.RaiseMessageReceived(new TransportChatMessage(
                    msg.MessageId.ToString("N")[..16],
                    msg.SenderNodeId.ToString(),
                    nodeGuid.ToString(),
                    msg.Text,
                    msg.SentAtUtc.ToUnixTimeMilliseconds()));
            };

            // Task 3: send inventory to peers that reconnect with known session history
            sp.GetRequiredService<PeerConnectionService>().PeerConnected += peerNodeId =>
            {
                var sessionId = sp.GetRequiredService<IMessageStore>().GetSessionIdForPeer(peerNodeId);
                if (sessionId.HasValue && Guid.TryParse(peerNodeId, out var peerGuid))
                    _ = router.OnPeerReconnectedAsync(peerGuid, sessionId.Value);
            };

            return router;
        });

        services.AddSingleton(sp =>
            new UdpDiscoveryService(config.NodeId, config.DisplayName, config.TcpPort, config.DiscoveryPort, config.IsRelay,
                sp.GetRequiredService<ILogger<UdpDiscoveryService>>()));

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
                sp.GetRequiredService<ILogger<UdpVoiceTransport>>(),
                config.VoicePort));

        services.AddSingleton(sp =>
            new MetricsService(config.NodeId,
                sp.GetRequiredService<PeerConnectionService>(),
                sp.GetRequiredService<ILogger<MetricsService>>()));

        services.AddSingleton(sp =>
            new ChannelService(config.NodeId,
                sp.GetRequiredService<PeerConnectionService>(),
                sp.GetRequiredService<ILogger<ChannelService>>()));

        return services;
    }
}