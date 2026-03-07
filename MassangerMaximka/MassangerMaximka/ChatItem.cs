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

    public LayoutOptions BubbleAlign => IsSystem ? LayoutOptions.Center : (IsFromMe ? LayoutOptions.End : LayoutOptions.Start);
    public Color BubbleBg => IsSystem ? Colors.Transparent : (IsFromMe ? Color.FromArgb("#000000") : Color.FromArgb("#2A2A2A"));
    public Color BubbleTextColor => IsSystem ? Color.FromArgb("#888888") : Color.FromArgb("#FFFFFF");
    public Color BubbleStrokeColor => IsSystem ? Colors.Transparent : (IsFromMe ? Colors.White : Color.FromArgb("#3A3A3A"));
    public Thickness BubblePadding => IsSystem ? new Thickness(0, 4) : new Thickness(14, 10);
    public int BubbleRadius => IsSystem ? 0 : 16;
    public int BubbleStrokeThickness => IsSystem ? 0 : 1;

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
