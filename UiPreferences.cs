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
    public Dictionary<string, string> NpcBackgroundFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string CurrentBgmFile { get; set; } = string.Empty;
    public double BgmVolume { get; set; } = 0.35;
    public bool BgmLoopEnabled { get; set; } = true;
    public bool SceneAutoSelectEnabled { get; set; } = false;

    public string GetBackgroundForNpc(string npcId) {
        if (string.IsNullOrWhiteSpace(npcId) || NpcBackgroundFiles.Count == 0) {
            return string.Empty;
        }

        return NpcBackgroundFiles.TryGetValue(npcId, out var file) ? file : string.Empty;
    }

    public void SetBackgroundForNpc(string npcId, string backgroundFile) {
        if (string.IsNullOrWhiteSpace(npcId)) {
            return;
        }

        if (string.IsNullOrWhiteSpace(backgroundFile)) {
            NpcBackgroundFiles.Remove(npcId);
            return;
        }

        NpcBackgroundFiles[npcId] = backgroundFile;
    }

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
