namespace MassangerMaximka
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var window = new Window(new AppShell())
            {
                Title = "Hex P2P Test"
            };
            
            #if MACCATALYST
            // Set minimum window size for Mac Catalyst
            window.MinimumWidth = 800;
            window.MinimumHeight = 600;
            #endif
            
            return window;
        }
    }
}