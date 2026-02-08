namespace Conversation;

using System.IO;
using System.Text.Json;

public sealed class UiPreferences {
    public double WindowLeft { get; set; } = 120;
    public double WindowTop { get; set; } = 80;
    public double WindowWidth { get; set; } = 1100;
    public double WindowHeight { get; set; } = 760;
    public string LastSessionId { get; set; } = "stilla";
    public string LastNpcId { get; set; } = "stilla";
    public int LastTurnsToLoad { get; set; } = 20;
    public bool AutoScrollEnabled { get; set; } = true;
    public double StandeePanelWidth { get; set; } = 360;
    public bool StandeeBackgroundDark { get; set; } = true;

    public static UiPreferences Load(string path) {
        try {
            if (!File.Exists(path)) return new UiPreferences();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<UiPreferences>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new UiPreferences();
        }
        catch {
            return new UiPreferences();
        }
    }

    public static async Task SaveAsync(string path, UiPreferences prefs) {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(prefs, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json);
    }
}
