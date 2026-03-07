using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MassangerMaximka;

public sealed class ChatItem : INotifyPropertyChanged
{
    public string Text { get; init; } = "";
    public string? VoicePath { get; init; }
    public byte[]? ImageBytes { get; init; }
    public bool IsVoice => VoicePath != null;
    public bool IsImage => ImageBytes != null;
    public bool IsText => VoicePath == null && ImageBytes == null;

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
