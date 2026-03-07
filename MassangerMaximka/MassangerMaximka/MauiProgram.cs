using HexTeam.Messenger.Core;
using HexTeam.Messenger.Core.Models;
using Microsoft.Extensions.Logging;
using Plugin.Maui.Audio;

namespace MassangerMaximka
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder.AddAudio();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                    fonts.AddFont("IBMPlexMono-Regular.ttf", "IBMPlexMonoRegular");
                    fonts.AddFont("IBMPlexMono-Medium.ttf", "IBMPlexMonoMedium");
                    fonts.AddFont("IBMPlexMono-Bold.ttf", "IBMPlexMonoBold");
                });

#if DEBUG
            builder.Logging.AddDebug();
#endif

            var tcpPort = GetPortFromEnvOrArgs();
            var discoveryPort = tcpPort - 2;
            var voicePort = tcpPort + 100;
            builder.Services.AddHexMessengerCore(new NodeConfiguration
            {
                DisplayName = DeviceInfo.Name,
                TcpPort = tcpPort,
                DiscoveryPort = discoveryPort,
                VoicePort = voicePort,
                IsRelay = false
            });

            return AppInstance = builder.Build();
        }

        public static MauiApp? AppInstance { get; private set; }

        private static int GetPortFromEnvOrArgs()
        {
            var env = Environment.GetEnvironmentVariable("HEX_TCP_PORT");
            if (!string.IsNullOrEmpty(env) && int.TryParse(env, out var port) && port > 1024 && port < 65535)
                return port;
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
                if ((args[i] == "--port" || args[i] == "-p" || args[i] == "--tcp-port") && int.TryParse(args[i + 1], out port) && port > 1024 && port < 65535)
                    return port;
            return 45680;
        }
    }
}
