namespace MassangerMaximka
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
            UserAppTheme = AppTheme.Dark;
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var window = new Window(new AppShell())
            {
                Title = "Ether.нет - не нужен интернет"
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