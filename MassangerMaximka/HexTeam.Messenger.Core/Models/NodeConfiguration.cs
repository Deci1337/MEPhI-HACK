namespace HexTeam.Messenger.Core.Models;

public sealed record NodeConfiguration
{
    public string NodeId { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public string DisplayName { get; init; } = Environment.MachineName;
    public int TcpPort { get; init; } = 45680;
    public bool IsRelay { get; init; } = false;
    public string ReceiveDirectory { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HexTeamReceived");
}
