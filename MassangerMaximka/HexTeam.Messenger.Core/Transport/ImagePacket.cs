namespace HexTeam.Messenger.Core.Transport;

public sealed class ImagePacket
{
    public string FileName { get; set; } = "";
    public string MimeType { get; set; } = "image/jpeg";
    public byte[] Data { get; set; } = [];
}
