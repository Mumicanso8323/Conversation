using System.IO;
using System.Text.Json;

namespace Conversation.Standee;

public sealed class StandeeConfig {
    public bool Enabled { get; set; } = true;
    public int MonitorIndex { get; set; } = 0;
    public bool Topmost { get; set; } = true;
    public bool ShowInTaskbar { get; set; } = false;
    public bool DebugVisibleBackground { get; set; } = false;
    public double Scale { get; set; } = 1.0;
    public bool ClickThrough { get; set; } = false;
    public StandeeWindowConfig Window { get; set; } = new();

    public static StandeeConfig LoadFromBaseDirectory() {
        var path = Path.Combine(AppContext.BaseDirectory, "standee_config.json");
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
}
