using System.IO;
using System.Text.Json;

namespace Conversation.Affinity;

public sealed class AffinityProfileRoot {
    public string DefaultNpcId { get; set; } = "stilla";
    public Dictionary<string, NpcProfile> Npcs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public NpcProfile GetRequiredProfile(string npcId) {
        if (Npcs.TryGetValue(npcId, out var profile)) {
            return profile;
        }

        if (Npcs.TryGetValue(DefaultNpcId, out var fallback)) {
            return fallback;
        }

        return new NpcProfile();
    }

    public static AffinityProfileRoot CreateDefault() => new() {
        DefaultNpcId = "stilla",
        Npcs = new Dictionary<string, NpcProfile>(StringComparer.OrdinalIgnoreCase) {
            ["stilla"] = new NpcProfile {
                DisplayName = "スティラ",
                Initial = new AffinityInitial {
                    Liked = 8,
                    Disliked = 3,
                    Trust = 25,
                    Respect = 15
                },
                Thresholds = new AffinityThresholds {
                    LoveOn = 75,
                    HateOn = 75,
                    LoveOff = 60,
                    HateOff = 60
                },
                Decay = new AffinityDecay {
                    Like = 0.65,
                    Dislike = 0.65
                },
                MemoryGain = new AffinityMemoryGain {
                    LikedFromLike = 0.35,
                    DislikedFromDislike = 0.35
                },
                StanceGain = new AffinityStanceGain {
                    LoveFromBalance = 0.08,
                    HateFromBalance = 0.08
                },
                Conversation = new AffinityConversationControl {
                    SilentChanceAtHighHate = 0.10,
                    ShortReplyChanceAtMidHate = 0.25,
                    EndConversationChance = 0.15
                }
            }
        }
    };
}

public sealed class NpcProfile {
    public string DisplayName { get; set; } = "NPC";
    public AffinityInitial Initial { get; set; } = new();
    public AffinityThresholds Thresholds { get; set; } = new();
    public AffinityDecay Decay { get; set; } = new();
    public AffinityMemoryGain MemoryGain { get; set; } = new();
    public AffinityStanceGain StanceGain { get; set; } = new();
    public AffinityWeakness Weakness { get; set; } = new();
    public AffinityConversationControl Conversation { get; set; } = new();
}

public sealed class AffinityInitial {
    public double Like { get; set; }
    public double Dislike { get; set; }
    public double Liked { get; set; }
    public double Disliked { get; set; }
    public double Love { get; set; }
    public double Hate { get; set; }
    public double Trust { get; set; }
    public double Respect { get; set; }
    public double SexualAwareness { get; set; }
}

public sealed class AffinityThresholds {
    public double LoveOn { get; set; } = 70;
    public double HateOn { get; set; } = 70;
    public double LoveOff { get; set; } = 55;
    public double HateOff { get; set; } = 55;
}

public sealed class AffinityDecay {
    public double Like { get; set; } = 0.70;
    public double Dislike { get; set; } = 0.70;
}

public sealed class AffinityMemoryGain {
    public double LikedFromLike { get; set; } = 0.40;
    public double DislikedFromDislike { get; set; } = 0.40;
}

public sealed class AffinityStanceGain {
    public double LoveFromBalance { get; set; } = 0.10;
    public double HateFromBalance { get; set; } = 0.10;
}

public sealed class AffinityWeakness {
    public double Approval { get; set; } = 1.0;
    public double Respect { get; set; } = 1.0;
    public double Food { get; set; } = 1.0;
    public double Money { get; set; } = 1.0;
    public double Sexual { get; set; } = 1.0;
}

public sealed class AffinityConversationControl {
    public double SilentChanceAtHighHate { get; set; } = 0.15;
    public double ShortReplyChanceAtMidHate { get; set; } = 0.35;
    public double EndConversationChance { get; set; } = 0.20;
}

public sealed class JsonAffinityProfileRepository {
    private readonly string _path;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public JsonAffinityProfileRepository(string path) {
        _path = path;
    }

    public async Task<AffinityProfileRoot> LoadAsync(CancellationToken ct = default) {
        if (!File.Exists(_path)) {
            var defaults = AffinityProfileRoot.CreateDefault();
            await SaveAsync(defaults, ct);
            return defaults;
        }

        await using var stream = File.OpenRead(_path);
        var root = await JsonSerializer.DeserializeAsync<AffinityProfileRoot>(stream, _jsonOptions, ct);
        return root ?? AffinityProfileRoot.CreateDefault();
    }

    public async Task SaveAsync(AffinityProfileRoot root, CancellationToken ct = default) {
        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, root, _jsonOptions, ct);
    }
}
