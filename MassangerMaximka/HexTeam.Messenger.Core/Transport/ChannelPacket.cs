namespace HexTeam.Messenger.Core.Transport;

public sealed class ChannelPacket
{
    public string ChannelId   { get; set; } = "";
    public string ChannelName { get; set; } = "";
    public string FromNodeId  { get; set; } = "";
    public List<string> MemberNodeIds { get; set; } = [];
}
