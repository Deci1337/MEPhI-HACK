namespace MassangerMaximka;

public partial class NearbyPage : ContentPage
{
    public NearbyPage()
    {
        InitializeComponent();
    }

    private void OnRefreshTapped(object sender, EventArgs e)
    {
        // TODO: trigger peer discovery rescan
    }

    private void OnConnectTapped(object sender, EventArgs e)
    {
        // TODO: open chat with selected peer
    }
}
