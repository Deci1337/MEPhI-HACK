using HexTeam.Messenger.Core;
using HexTeam.Messenger.Core.Models;
using Microsoft.Extensions.Logging;

namespace MassangerMaximka
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

#if DEBUG
            builder.Logging.AddDebug();
#endif

            builder.Services.AddHexMessengerCore(new NodeConfiguration
            {
                DisplayName = DeviceInfo.Name,
                TcpPort = 45680,
                IsRelay = false
            });

            return AppInstance = builder.Build();
        }

        public static MauiApp? AppInstance { get; private set; }
    }
}
