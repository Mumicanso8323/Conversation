namespace Conversation.Standee;

using Conversation.Diagnostics;
using System.Windows;

public sealed class StandeeService : Conversation.IStandeeController {
    private StandeeWindow? _window;
    public StandeeConfig Config { get; }
    public string? LastWarning { get; private set; }

    public StandeeService() {
        Config = StandeeConfig.LoadFromBaseDirectory();
    }

    public void ApplyRuntimeSettings(AppSettings settings) {
        try {
            Config.Enabled = settings.Standee.Enabled;
            Config.MonitorIndex = settings.Standee.MonitorIndex;
        }
        catch (Exception ex) {
            Log.Error(ex, "ApplyRuntimeSettings");
        }
    }

    public Task StartAsync() {
        LastWarning = null;
        if (Config.Enabled && Config.Scale <= 0) {
            Config.Enabled = false;
            LastWarning = "standee_config.json has invalid scale <= 0; standee disabled.";
            Log.Warn(LastWarning);
        }

        if (!Config.Enabled || _window is not null) {
            return Task.CompletedTask;
        }

        return Application.Current.Dispatcher.InvokeAsync(() => {
            _window = new StandeeWindow(Config);
            _window.Show();
        }).Task;
    }

    public Task StopAsync() {
        return Application.Current.Dispatcher.InvokeAsync(() => {
            if (_window is null) {
                return;
            }

            _window.Close();
            _window = null;
        }).Task;
    }

    public Task HideAsync(CancellationToken ct = default)
        => Application.Current.Dispatcher.InvokeAsync(() => _window?.Hide()).Task;

    public Task ShowAsync(CancellationToken ct = default) {
        if (!Config.Enabled) {
            return Task.CompletedTask;
        }

        return Application.Current.Dispatcher.InvokeAsync(() => {
            if (_window is null) {
                _window = new StandeeWindow(Config);
            }

            _window.Show();
        }).Task;
    }

    public Task SetSpriteAsync(string fileName, CancellationToken ct = default) {
        var safe = StandeeSprites.NormalizeOrDefault(fileName);
        return Application.Current.Dispatcher.InvokeAsync(() => {
            if (_window is null || !Config.Enabled) {
                return;
            }

            _window.LoadSprite(safe);
        }).Task;
    }
}
