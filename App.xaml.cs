namespace Conversation;

using Conversation.Diagnostics;
using Conversation.Standee;
using System.Windows;

public partial class App : Application {
    public StandeeService StandeeService { get; } = new();
    public List<string> StartupWarnings { get; } = new();

    protected override void OnStartup(StartupEventArgs e) {
        base.OnStartup(e);
        Log.Info($"DataRoot selected: {AppPaths.DataRoot}");

        try {
            var settings = AppSettings.Load();
            StandeeService.ApplyRuntimeSettings(settings);
            _ = StandeeService.StartAsync();
            if (!string.IsNullOrWhiteSpace(StandeeService.LastWarning)) {
                StartupWarnings.Add(StandeeService.LastWarning);
            }
        }
        catch (Exception ex) {
            Log.Error(ex, "App.OnStartup");
        }
    }

    protected override void OnExit(ExitEventArgs e) {
        try {
            _ = StandeeService.StopAsync();
        }
        catch (Exception ex) {
            Log.Error(ex, "App.OnExit");
        }

        base.OnExit(e);
    }
}
