using System.Text;
using Conversation.Affinity;

namespace Conversation.Psyche;

public sealed class StateNarrator {
    public string BuildJudgeStateText(string npcId, PsycheProfileConfig profile, AffinityState affinity, PsycheState psyche, string userText, string recentContext) {
        var builder = new StringBuilder();
        builder.AppendLine("[JUDGE STATE: PSYCHE]");
        builder.AppendLine("Mood:");
        builder.AppendLine($"* {MoodValenceFragment(profile, psyche)}");
        builder.AppendLine($"* {MoodArousalFragment(profile, psyche)}");
        builder.AppendLine($"* {MoodControlFragment(profile, psyche)}");

        var desireLines = SalientDesires(profile, psyche).ToList();
        var libidoLines = SalientLibido(profile, psyche, affinity, userText, recentContext).ToList();

        builder.AppendLine("Desires (salient):");
        if (desireLines.Count == 0) builder.AppendLine("* 顕著な内的圧は検出されない。通常運転。");
        foreach (var line in desireLines) builder.AppendLine($"* {line}");

        builder.AppendLine("Libido (salient, gated):");
        if (libidoLines.Count == 0) builder.AppendLine("* 顕著な内的圧は検出されない。通常運転。");
        foreach (var line in libidoLines) builder.AppendLine($"* {line}");

        return builder.ToString().Trim();
    }

    private static IEnumerable<string> SalientDesires(PsycheProfileConfig profile, PsycheState state) {
        var candidates = Enum.GetValues<DesireAxis>()
            .Select(axis => CandidateForDesire(profile, state, axis))
            .Where(x => x is not null)
            .Select(x => x!)
            .OrderByDescending(x => x.Salience)
            .Take(profile.K.KDesire)
            .Select(x => x.Fragment);
        return candidates;
    }

    private static IEnumerable<string> SalientLibido(PsycheProfileConfig profile, PsycheState state, AffinityState affinity, string userText, string recentContext) {
        if (!PassesLibidoGate(profile, affinity, userText, recentContext)) return Enumerable.Empty<string>();

        return Enum.GetValues<LibidoAxis>()
            .Select(axis => CandidateForLibido(profile, state, axis))
            .Where(x => x is not null)
            .Select(x => x!)
            .OrderByDescending(x => x.Salience)
            .Take(profile.K.KLibido)
            .Select(x => x.Fragment);
    }

    private static Candidate? CandidateForDesire(PsycheProfileConfig profile, PsycheState state, DesireAxis axis) {
        var axisProfile = profile.Desires[axis];
        var level = DetermineLevel(PsycheOrchestrator.Effective(state.DesireTrait[axis], state.DesireDeficit[axis]), axisProfile);
        if (level < profile.MinPromptLevel.Desire) return null;
        if (!profile.DesireLexicon[axis].PromptFragmentByLevel.TryGetValue(level, out var fragment)) return null;
        var weight = profile.DesireLexicon[axis].ImpactWeightByLevel.TryGetValue(level, out var w) ? w : 1.0;
        return new Candidate(fragment, weight);
    }

    private static Candidate? CandidateForLibido(PsycheProfileConfig profile, PsycheState state, LibidoAxis axis) {
        var axisProfile = profile.Libido[axis];
        var level = DetermineLevel(PsycheOrchestrator.Effective(state.Libido.Trait[axis], state.Libido.Deficit[axis]), axisProfile);
        if (level < profile.MinPromptLevel.Libido) return null;
        if (!profile.LibidoLexicon[axis].PromptFragmentByLevel.TryGetValue(level, out var fragment)) return null;
        var weight = profile.LibidoLexicon[axis].ImpactWeightByLevel.TryGetValue(level, out var w) ? w : 1.0;
        return new Candidate(fragment, weight);
    }

    private static bool PassesLibidoGate(PsycheProfileConfig profile, AffinityState affinity, string userText, string recentContext) {
        if (affinity.Hate >= profile.LibidoGate.MaxHate) return false;
        if (affinity.Trust < profile.LibidoGate.MinTrust) return false;

        var merged = (userText + "\n" + recentContext).ToLowerInvariant();
        return profile.LibidoGate.SexualKeywords.Any(k => merged.Contains(k.ToLowerInvariant()));
    }

    private static PsycheLevel DetermineLevel(double effective, AxisProfile axis) {
        foreach (var pair in axis.LevelThresholds.OrderByDescending(x => x.Key)) {
            if (effective >= pair.Value.Min && effective <= pair.Value.Max) return pair.Key;
        }
        return PsycheLevel.None;
    }

    private static string MoodValenceFragment(PsycheProfileConfig profile, PsycheState state) {
        if (state.Mood.CurrentValence > 2) return profile.MoodLexicon.PositiveValence;
        if (state.Mood.CurrentValence < -2) return profile.MoodLexicon.NegativeValence;
        return profile.MoodLexicon.NeutralValence;
    }

    private static string MoodArousalFragment(PsycheProfileConfig profile, PsycheState state) =>
        state.Mood.CurrentArousal >= 6 ? profile.MoodLexicon.HighArousal : profile.MoodLexicon.LowArousal;

    private static string MoodControlFragment(PsycheProfileConfig profile, PsycheState state) =>
        state.Mood.CurrentControl >= 5 ? profile.MoodLexicon.HighControl : profile.MoodLexicon.LowControl;

    private sealed record Candidate(string Fragment, double Salience);
}
