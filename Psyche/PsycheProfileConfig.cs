using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conversation.Psyche;

public sealed class PsycheProfileRoot {
    public string DefaultNpcId { get; set; } = "stilla";
    public Dictionary<string, PsycheProfileConfig> Npcs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public PsycheProfileConfig GetRequiredProfile(string npcId) {
        if (Npcs.TryGetValue(npcId, out var profile)) return profile;
        if (Npcs.TryGetValue(DefaultNpcId, out var fallback)) return fallback;
        return PsycheProfileConfig.CreateDefault();
    }

    public static PsycheProfileRoot CreateDefault() => new() {
        Npcs = new Dictionary<string, PsycheProfileConfig>(StringComparer.OrdinalIgnoreCase) {
            ["stilla"] = PsycheProfileConfig.CreateDefault()
        }
    };
}

public sealed class PsycheProfileConfig {
    public Dictionary<DesireAxis, AxisProfile> Desires { get; set; } = Enum.GetValues<DesireAxis>().ToDictionary(x => x, _ => new AxisProfile());
    public Dictionary<LibidoAxis, AxisProfile> Libido { get; set; } = Enum.GetValues<LibidoAxis>().ToDictionary(x => x, _ => new AxisProfile());
    public MoodProfile Mood { get; set; } = new();
    public PsycheDecay Decay { get; set; } = new();
    public TopKConfig K { get; set; } = new();
    public MinPromptLevelConfig MinPromptLevel { get; set; } = new();
    public Dictionary<DesireAxis, LevelLexicon> DesireLexicon { get; set; } = Enum.GetValues<DesireAxis>().ToDictionary(x => x, _ => LevelLexicon.CreateDefault(x.ToString()));
    public Dictionary<LibidoAxis, LevelLexicon> LibidoLexicon { get; set; } = Enum.GetValues<LibidoAxis>().ToDictionary(x => x, _ => LevelLexicon.CreateDefault(x.ToString()));
    public MoodLexicon MoodLexicon { get; set; } = new();
    public LibidoGateConfig LibidoGate { get; set; } = new();

    public static PsycheProfileConfig CreateDefault() => new();
}

public sealed class AxisProfile {
    public double Trait { get; set; } = 4;
    public double InitialDeficit { get; set; } = 0;
    public double Gain { get; set; } = 1;
    public Dictionary<PsycheLevel, LevelThreshold> LevelThresholds { get; set; } = new() {
        [PsycheLevel.Low] = new LevelThreshold(2,4),
        [PsycheLevel.Mid] = new LevelThreshold(5,6),
        [PsycheLevel.High] = new LevelThreshold(7,8),
        [PsycheLevel.Extreme] = new LevelThreshold(9,10)
    };
}

public sealed record LevelThreshold(double Min, double Max);

public sealed class MoodProfile {
    public double InitialValence { get; set; }
    public double InitialArousal { get; set; } = 5;
    public double InitialControl { get; set; } = 5;
    public double BaselineValence { get; set; }
    public double BaselineArousal { get; set; } = 5;
    public double BaselineControl { get; set; } = 5;
}

public sealed class PsycheDecay {
    public double DesireDeficitDecayPerTurn { get; set; } = 0.1;
    public double LibidoDeficitDecayPerTurn { get; set; } = 0.05;
    public double MoodValenceRecoveryRate { get; set; } = 0.25;
    public double MoodArousalRecoveryRate { get; set; } = 0.35;
    public double MoodControlRecoveryRate { get; set; } = 0.35;
}

public sealed class TopKConfig {
    public int KDesire { get; set; } = 3;
    public int KLibido { get; set; } = 1;
}

public sealed class MinPromptLevelConfig {
    public PsycheLevel Desire { get; set; } = PsycheLevel.High;
    public PsycheLevel Libido { get; set; } = PsycheLevel.High;
}

public enum PsycheLevel { None = 0, Low = 1, Mid = 2, High = 3, Extreme = 4 }

public sealed class LevelLexicon {
    public Dictionary<PsycheLevel, double> ImpactWeightByLevel { get; set; } = new() {
        [PsycheLevel.High] = 1.0,
        [PsycheLevel.Extreme] = 1.5
    };
    public Dictionary<PsycheLevel, string> PromptFragmentByLevel { get; set; } = new() {
        [PsycheLevel.High] = "内的圧が高まっている。",
        [PsycheLevel.Extreme] = "内的圧が極めて高く、優先的に扱う必要がある。"
    };

    public static LevelLexicon CreateDefault(string axis) => new() {
        PromptFragmentByLevel = new() {
            [PsycheLevel.High] = $"{axis}関連の内的要求が高く、会話内容に反映したい。",
            [PsycheLevel.Extreme] = $"{axis}関連の内的要求が極めて高く、優先反映が必要。"
        }
    };
}

public sealed class MoodLexicon {
    public string PositiveValence { get; set; } = "快方向で、受容的に解釈しやすい。";
    public string NegativeValence { get; set; } = "不快方向で、防衛的に解釈しやすい。";
    public string NeutralValence { get; set; } = "感情価は中立。";
    public string HighArousal { get; set; } = "覚醒度が高く、反応は鋭い。";
    public string LowArousal { get; set; } = "覚醒度が低く、反応は慎重。";
    public string HighControl { get; set; } = "制御感が高く、境界維持がしやすい。";
    public string LowControl { get; set; } = "制御感が低く、境界維持を強める必要。";
}

public sealed class LibidoGateConfig {
    public List<string> SexualKeywords { get; set; } = ["sex", "キス", "裸", "えっち", "抱", "乳", "陰茎", "挿入"];
    public double MinTrust { get; set; } = 20;
    public double MaxHate { get; set; } = 70;
}

public sealed class JsonPsycheProfileRepository {
    private readonly string _path;

    public JsonPsycheProfileRepository(string path) {
        _path = path;
    }

    public async Task<PsycheProfileRoot> LoadAsync(CancellationToken ct = default) {
        if (!File.Exists(_path)) {
            var defaults = PsycheProfileRoot.CreateDefault();
            await SaveAsync(defaults, ct);
            return defaults;
        }

        await using var stream = File.OpenRead(_path);
        var root = await JsonSerializer.DeserializeAsync<PsycheProfileRoot>(stream, PsycheJson.Options, ct);
        return root ?? PsycheProfileRoot.CreateDefault();
    }

    public async Task SaveAsync(PsycheProfileRoot root, CancellationToken ct = default) {
        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, root, PsycheJson.Options, ct);
    }
}

public static class PsycheJson {
    public static JsonSerializerOptions Options { get; } = new() {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };
}
