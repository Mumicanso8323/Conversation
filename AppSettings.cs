namespace Conversation;

using System.IO;
using System.Text.Json;

public sealed class AppSettings {
    public ModelSettings Models { get; set; } = new();
    public StandeeSettings Standee { get; set; } = new();

    public static AppSettings Load() {
        try {
            if (!File.Exists(AppPaths.SettingsFilePath)) {
                return new AppSettings();
            }

            var json = File.ReadAllText(AppPaths.SettingsFilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppSettings();
        }
        catch {
            return new AppSettings();
        }
    }

    public void Save() {
        try {
            Directory.CreateDirectory(AppPaths.SettingsDir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(AppPaths.SettingsFilePath, json);
        }
        catch {
            // ignore
        }
    }
}

public sealed class ModelSettings {
    public string MainChat { get; set; } = "gpt-5.2";
    public string StandeeJudge { get; set; } = "gpt-5.1";
}

public sealed class StandeeSettings {
    public bool Enabled { get; set; } = true;
    public int MonitorIndex { get; set; } = 0;
}
