namespace MassangerMaximka;

public sealed class ChatItem
{
    public string Text { get; init; } = "";
    public string? VoicePath { get; init; }
    public bool IsVoice => VoicePath != null;
    public bool IsText => VoicePath == null;
}
