using System.Text.Json;

namespace Conversation.Affinity;

public sealed class AffinityProfileRoot {
    public string DefaultNpcId { get; set; } = "default";
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
        DefaultNpcId = "mio",
        Npcs = new Dictionary<string, NpcProfile>(StringComparer.OrdinalIgnoreCase) {
            ["mio"] = new NpcProfile {
                DisplayName = "ミオ",
                Initial = new AffinityInitial { Liked = 10, Disliked = 5, Trust = 20, Respect = 10 },
                Thresholds = new AffinityThresholds { LoveOn = 70, HateOn = 70, LoveOff = 55, HateOff = 55 },
                Decay = new AffinityDecay { Like = 0.70, Dislike = 0.70 },
                MemoryGain = new AffinityMemoryGain { LikedFromLike = 0.40, DislikedFromDislike = 0.40 },
                StanceGain = new AffinityStanceGain { LoveFromBalance = 0.10, HateFromBalance = 0.10 },
                Conversation = new AffinityConversationControl {
                    SilentChanceAtHighHate = 0.15,
                    ShortReplyChanceAtMidHate = 0.35,
                    EndConversationChance = 0.20
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
