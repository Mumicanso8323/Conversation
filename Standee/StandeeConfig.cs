using System.Text.Json;

namespace Conversation.Standee;

public sealed class StandeeConfig {
    public bool Enabled { get; set; } = true;
    public int MonitorIndex { get; set; } = 1;
    public string PipeName { get; set; } = "conversation-standee";
    public bool AlwaysOnTop { get; set; } = true;
    public StandeeWindowConfig Window { get; set; } = new();

    public static StandeeConfig LoadOrDefault(string path) {
        try {
            if (!File.Exists(path)) {
                return new StandeeConfig();
            }

            var json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<StandeeConfig>(json, new JsonSerializerOptions {
                PropertyNameCaseInsensitive = true
            });
            return cfg ?? new StandeeConfig();
        }
        catch {
            return new StandeeConfig();
        }
    }
}

public sealed class StandeeWindowConfig {
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public double? Scale { get; set; } = 1.0;
}
