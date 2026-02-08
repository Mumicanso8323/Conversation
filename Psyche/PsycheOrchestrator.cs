namespace Conversation.Psyche;

public sealed class PsycheOrchestrator {
    public async Task<PsycheState> LoadOrCreateAsync(string npcId, PsycheProfileConfig profile, IPsycheStore store, CancellationToken ct) {
        var loaded = await store.LoadAsync(npcId, ct);
        if (loaded is not null) return loaded;

        var created = new PsycheState {
            NpcId = npcId,
            DesireTrait = profile.Desires.ToDictionary(kv => kv.Key, kv => kv.Value.Trait),
            DesireDeficit = profile.Desires.ToDictionary(kv => kv.Key, kv => kv.Value.InitialDeficit),
            DesireGain = profile.Desires.ToDictionary(kv => kv.Key, kv => kv.Value.Gain),
            Libido = new LibidoState {
                Trait = profile.Libido.ToDictionary(kv => kv.Key, kv => kv.Value.Trait),
                Deficit = profile.Libido.ToDictionary(kv => kv.Key, kv => kv.Value.InitialDeficit),
                Gain = profile.Libido.ToDictionary(kv => kv.Key, kv => kv.Value.Gain)
            },
            Mood = new MoodState {
                CurrentValence = profile.Mood.InitialValence,
                CurrentArousal = profile.Mood.InitialArousal,
                CurrentControl = profile.Mood.InitialControl,
                BaselineValence = profile.Mood.BaselineValence,
                BaselineArousal = profile.Mood.BaselineArousal,
                BaselineControl = profile.Mood.BaselineControl
            },
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await store.SaveAsync(created, ct);
        return created;
    }

    public void ApplyDelta(PsycheState state, PsycheProfileConfig profile, PsycheDelta delta) {
        foreach (var axis in Enum.GetValues<DesireAxis>()) {
            state.DesireDeficit[axis] = Math.Max(0, Get(state.DesireDeficit, axis) - profile.Decay.DesireDeficitDecayPerTurn);
        }

        foreach (var axis in Enum.GetValues<LibidoAxis>()) {
            state.Libido.Deficit[axis] = Math.Max(0, Get(state.Libido.Deficit, axis) - profile.Decay.LibidoDeficitDecayPerTurn);
        }

        state.Mood.CurrentValence += (state.Mood.BaselineValence - state.Mood.CurrentValence) * profile.Decay.MoodValenceRecoveryRate;
        state.Mood.CurrentArousal += (state.Mood.BaselineArousal - state.Mood.CurrentArousal) * profile.Decay.MoodArousalRecoveryRate;
        state.Mood.CurrentControl += (state.Mood.BaselineControl - state.Mood.CurrentControl) * profile.Decay.MoodControlRecoveryRate;

        foreach (var item in delta.DesireDeficit) {
            state.DesireDeficit[item.Key] = Clamp01Ten(Get(state.DesireDeficit, item.Key) + item.Value * Get(state.DesireGain, item.Key));
        }

        foreach (var item in delta.LibidoDeficit) {
            state.Libido.Deficit[item.Key] = Clamp01Ten(Get(state.Libido.Deficit, item.Key) + item.Value * Get(state.Libido.Gain, item.Key));
        }

        state.Mood.CurrentValence = Clamp(state.Mood.CurrentValence + delta.Mood.Valence, -10, 10);
        state.Mood.CurrentArousal = Clamp(state.Mood.CurrentArousal + delta.Mood.Arousal, 0, 10);
        state.Mood.CurrentControl = Clamp(state.Mood.CurrentControl + delta.Mood.Control, 0, 10);
        state.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public static double Effective(double trait, double deficit) => Clamp01Ten(trait + deficit);

    private static double Get<T>(Dictionary<T, double> map, T key) where T : notnull => map.TryGetValue(key, out var value) ? value : 0;
    private static double Clamp01Ten(double value) => Clamp(value, 0, 10);
    private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));
}
