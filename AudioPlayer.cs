namespace Conversation;

using System.Windows.Media;

public sealed class AudioPlayer : IDisposable {
    private readonly MediaPlayer _mediaPlayer = new();
    private bool _disposed;
    private bool _loopEnabled = true;
    private string _currentPath = string.Empty;

    public AudioPlayer() {
        _mediaPlayer.MediaEnded += OnMediaEnded;
        _mediaPlayer.MediaFailed += (_, _) => { };
    }

    public double Volume {
        get => _mediaPlayer.Volume;
        set => _mediaPlayer.Volume = Math.Clamp(value, 0, 1);
    }

    public bool LoopEnabled {
        get => _loopEnabled;
        set => _loopEnabled = value;
    }

    public string CurrentPath => _currentPath;

    public void Play(string filePath) {
        if (_disposed || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) {
            return;
        }

        _currentPath = filePath;
        _mediaPlayer.Open(new Uri(filePath, UriKind.Absolute));
        if (Volume <= 0) {
            _mediaPlayer.Pause();
            return;
        }

        _mediaPlayer.Play();
    }

    public void Pause() {
        if (_disposed) return;
        _mediaPlayer.Pause();
    }

    public void Stop() {
        if (_disposed) return;
        _mediaPlayer.Stop();
    }

    private void OnMediaEnded(object? sender, EventArgs e) {
        if (!_loopEnabled || _disposed || string.IsNullOrWhiteSpace(_currentPath) || !File.Exists(_currentPath)) {
            return;
        }

        _mediaPlayer.Position = TimeSpan.Zero;
        if (Volume > 0) {
            _mediaPlayer.Play();
        }
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _mediaPlayer.MediaEnded -= OnMediaEnded;
        _mediaPlayer.Close();
    }
}
