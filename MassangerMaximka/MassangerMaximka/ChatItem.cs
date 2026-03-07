using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MassangerMaximka;

public sealed class ChatItem : INotifyPropertyChanged
{
    public string Text { get; init; } = "";
    public string? VoicePath { get; init; }
    public byte[]? ImageBytes { get; init; }
    public bool IsFromMe { get; init; }
    public bool IsSystem { get; init; }
    public bool IsVoice => VoicePath != null;
    public bool IsImage => ImageBytes != null;
    public bool IsText => VoicePath == null && ImageBytes == null;
    public LayoutOptions BubbleAlign => IsFromMe ? LayoutOptions.End : LayoutOptions.Start;
    public Color BubbleColor => IsFromMe ? Color.FromArgb("#2B4C8C")
        : IsSystem ? Color.FromArgb("#1C1E28")
        : Color.FromArgb("#252836");

    private string? _status;
    public string? Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasStatus)); }
    }

    public bool HasStatus => _status != null;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
