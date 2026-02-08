namespace Conversation.Standee;

using Conversation.Diagnostics;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Windows.Media.Imaging;

public sealed class StandeeService : Conversation.IStandeeController, INotifyPropertyChanged {
    private readonly ConcurrentDictionary<string, BitmapImage> _imageCache = new(StringComparer.OrdinalIgnoreCase);

    private BitmapImage? _currentBitmapImage;
    private string? _currentSpritePath;
    private bool _standeeVisible;

    public event PropertyChangedEventHandler? PropertyChanged;

    public StandeeConfig Config { get; }
    public string? LastWarning { get; private set; }

    public BitmapImage? CurrentBitmapImage {
        get => _currentBitmapImage;
        private set {
            if (ReferenceEquals(_currentBitmapImage, value)) {
                return;
            }

            _currentBitmapImage = value;
            OnPropertyChanged(nameof(CurrentBitmapImage));
        }
    }

    public string? CurrentSpritePath {
        get => _currentSpritePath;
        private set {
            if (string.Equals(_currentSpritePath, value, StringComparison.Ordinal)) {
                return;
            }

            _currentSpritePath = value;
            OnPropertyChanged(nameof(CurrentSpritePath));
        }
    }

    public bool StandeeVisible {
        get => _standeeVisible;
        private set {
            if (_standeeVisible == value) {
                return;
            }

            _standeeVisible = value;
            OnPropertyChanged(nameof(StandeeVisible));
        }
    }

    public StandeeService() {
        Config = StandeeConfig.LoadFromBaseDirectory();
    }

    public void ApplyRuntimeSettings(AppSettings settings) {
        try {
            Config.Enabled = settings.Standee.Enabled;
            SetStandeeVisible(Config.Enabled);
        }
        catch (Exception ex) {
            Log.Error(ex, "ApplyRuntimeSettings");
        }
    }

    public async Task StartAsync() {
        LastWarning = null;
        if (Config.Enabled && Config.Scale <= 0) {
            Config.Enabled = false;
            LastWarning = "standee_config.json の scale が 0 以下のため、立ち絵表示を無効化しました。";
            Log.Warn(LastWarning);
        }

        SetStandeeVisible(Config.Enabled);
        await SetSpriteAsync(StandeeSprites.Default);
    }

    public Task StopAsync() => Task.CompletedTask;

    public Task HideAsync(CancellationToken ct = default) {
        SetStandeeVisible(false);
        return Task.CompletedTask;
    }

    public Task ShowAsync(CancellationToken ct = default) {
        if (!Config.Enabled) {
            return Task.CompletedTask;
        }

        SetStandeeVisible(true);
        return Task.CompletedTask;
    }

    public Task SetSpriteAsync(string fileName, CancellationToken ct = default) {
        if (ct.IsCancellationRequested) {
            return Task.FromCanceled(ct);
        }

        var safe = StandeeSprites.NormalizeOrDefault(fileName);
        var baseStandeeDir = Path.Combine(AppPaths.EffectiveAssetsDir, "standee");
        var spritePath = Path.Combine(baseStandeeDir, safe);

        if (!File.Exists(spritePath)) {
            spritePath = Path.Combine(baseStandeeDir, StandeeSprites.Default);
            if (!File.Exists(spritePath)) {
                UpdateSprite(null, null);
                return Task.CompletedTask;
            }
        }

        try {
            var image = _imageCache.GetOrAdd(spritePath, LoadBitmap);
            UpdateSprite(spritePath, image);
        }
        catch (Exception ex) {
            Log.Error(ex, "SetSpriteAsync");
            UpdateSprite(null, null);
        }

        return Task.CompletedTask;
    }


    private void SetStandeeVisible(bool visible) {
        RunOnUi(() => StandeeVisible = visible);
    }

    private void UpdateSprite(string? path, BitmapImage? image) {
        RunOnUi(() => {
            CurrentSpritePath = path;
            CurrentBitmapImage = image;
        });
    }

    private static void RunOnUi(Action action) {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }
    private static BitmapImage LoadBitmap(string absolutePath) {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(absolutePath, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
