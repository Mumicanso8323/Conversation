namespace Conversation.ViewModels;

using System.ComponentModel;

public sealed class ChatMessage : INotifyPropertyChanged {
    private string _role = "system";
    private string _speaker = string.Empty;
    private string _text = string.Empty;
    private DateTime? _timestamp;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Role {
        get => _role;
        set {
            if (_role == value) return;
            _role = value;
            OnPropertyChanged(nameof(Role));
        }
    }

    public string Speaker {
        get => _speaker;
        set {
            if (_speaker == value) return;
            _speaker = value;
            OnPropertyChanged(nameof(Speaker));
            OnPropertyChanged(nameof(DisplayText));
        }
    }

    public string Text {
        get => _text;
        set {
            if (_text == value) return;
            _text = value;
            OnPropertyChanged(nameof(Text));
            OnPropertyChanged(nameof(DisplayText));
        }
    }

    public DateTime? Timestamp {
        get => _timestamp;
        set {
            if (_timestamp == value) return;
            _timestamp = value;
            OnPropertyChanged(nameof(Timestamp));
        }
    }

    public string DisplayText => string.IsNullOrWhiteSpace(Speaker) ? Text : $"{Speaker} > {Text}";

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
