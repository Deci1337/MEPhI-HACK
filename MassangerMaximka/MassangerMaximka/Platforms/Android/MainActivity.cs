using Android.App;
using Android.Content.PM;
using Android.Net.Wifi;
using Android.OS;
using Android.Views;

namespace MassangerMaximka
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, WindowSoftInputMode = SoftInput.AdjustResize, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        private WifiManager.MulticastLock? _multicastLock;
        private WifiManager.WifiLock? _wifiLock;

        protected override void OnResume()
        {
            base.OnResume();
            AcquireWifiLocks();
        }

        protected override void OnPause()
        {
            ReleaseWifiLocks();
            base.OnPause();
        }

        private void AcquireWifiLocks()
        {
            try
            {
                var wifiManager = (WifiManager?)GetSystemService(WifiService);
                if (wifiManager == null) return;

                if (_multicastLock == null)
                {
                    _multicastLock = wifiManager.CreateMulticastLock("photon_voice_multicast");
                    _multicastLock.SetReferenceCounted(false);
                }
                if (!_multicastLock.IsHeld)
                    _multicastLock.Acquire();

                if (_wifiLock == null)
                {
#pragma warning disable CA1422
                    _wifiLock = wifiManager.CreateWifiLock(Android.Net.WifiMode.FullHighPerf, "photon_voice_wifi");
#pragma warning restore CA1422
                    _wifiLock.SetReferenceCounted(false);
                }
                if (!_wifiLock.IsHeld)
                    _wifiLock.Acquire();
            }
            catch { }
        }

        private void ReleaseWifiLocks()
        {
            try { if (_multicastLock?.IsHeld == true) _multicastLock.Release(); } catch { }
            try { if (_wifiLock?.IsHeld == true) _wifiLock.Release(); } catch { }
        }
    }
}
