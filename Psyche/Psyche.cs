namespace Conversation.Psyche;

public enum SocialStance { Neutral, Love, Hate }
public enum SceneDecayMode { None, SoftDecay, HardResetRecentFlags }

public interface IAffinityView {
    double InterpretationBias(string actorId);
    double Trust(string actorId);
    SocialStance Stance(string actorId);
    double SafetyWith(string actorId);
    double SexualAwareness(string actorId);
    double Respect(string actorId);
    double Resentment(string actorId);
}

public readonly record struct Stimulus(
    string Id,
    DateTimeOffset Timestamp,
    string ActorId,
    string TargetId,
    IReadOnlyCollection<string> Context,
    double Intensity,
    double Valence,
    IReadOnlyCollection<string> Tags,
    IReadOnlyCollection<string>? Channels = null,
    bool IsIntentional = true,
    double Ambiguity = 0,
    double ConsentRelated = 0,
    double ViolationLevel = 0,
    double Novelty = 0,
    double SocialCost = 0,
    TimeSpan? TimePassed = null
);

public readonly record struct WorldContext(IReadOnlyCollection<string> Tags);
public readonly record struct SceneStep(TimeSpan Elapsed, SceneDecayMode DecayMode = SceneDecayMode.SoftDecay);

public sealed record ExpressionStyle(
    double Expressiveness = 0.5,
    double Warmth = 0.5,
    double Sarcasm = 0,
    double AggressionMasking = 0.3,
    double ShameMasking = 0.3,
    double PolitenessBaseline = 0.5
);

public sealed class Temperament {
    public Dictionary<string, double> BaselineEmotion { get; } = PsycheMath.KeysToValue(PsycheModel.EmotionKeys, 0);
    public Dictionary<string, double> BaselineMood { get; } = new() {
        ["good_mood"] = 0.5, ["irritability"] = 0.2, ["anxiety"] = 0.2, ["melancholy"] = 0.15,
        ["confidence"] = 0.5, ["loneliness"] = 0.2, ["rested"] = 0.6
    };
    public Dictionary<string, double> BaselineDrive { get; } = PsycheMath.KeysToValue(PsycheModel.DriveKeys, 0.2);
    public Dictionary<string, double> EmotionGain { get; } = PsycheMath.KeysToValue(PsycheModel.EmotionKeys, 1);
    public Dictionary<string, double> MoodGain { get; } = PsycheMath.KeysToValue(PsycheModel.MoodKeys, 1);
    public Dictionary<string, double> DriveGain { get; } = PsycheMath.KeysToValue(PsycheModel.DriveKeys, 1);
    public Dictionary<string, double> SensitivityToTag { get; } = PsycheMath.KeysToValue(PsycheModel.DefaultTags, 1);

    public Dictionary<string, TimeSpan> EmotionHalfLife { get; } = PsycheModel.CreateDefaultEmotionHalfLife();
    public Dictionary<string, TimeSpan> MoodHalfLife { get; } = PsycheModel.CreateDefaultMoodHalfLife();
    public Dictionary<string, TimeSpan> RecentFlagHalfLife { get; } = PsycheMath.KeysToValue(PsycheModel.RecentFlagKeys, TimeSpan.FromHours(1));

    public TimeSpan StressHalfLife { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan ArousalHalfLife { get; set; } = TimeSpan.FromMinutes(15);

    public Dictionary<string, double> DriveAccumulationPerSecond { get; } = PsycheModel.CreateDefaultDriveAccumulation();
}

public sealed class Psyche {
    private readonly Temperament _temperament;
    private readonly ExpressionStyle _style;

    private readonly Dictionary<string, double> _emotion = PsycheMath.KeysToValue(PsycheModel.EmotionKeys, 0);
    private readonly Dictionary<string, double> _mood = PsycheMath.KeysToValue(PsycheModel.MoodKeys, 0);
    private readonly Dictionary<string, double> _drive = PsycheMath.KeysToValue(PsycheModel.DriveKeys, 0);
    private readonly Dictionary<string, double> _regulationBase = PsycheModel.CreateDefaultRegulation();
    private readonly Dictionary<string, double> _regulationEffective = PsycheModel.CreateDefaultRegulation();
    private readonly Dictionary<string, double> _recentFlags = PsycheMath.KeysToValue(PsycheModel.RecentFlagKeys, 0);

    private readonly Dictionary<string, string> _lastImpulseContributions = new();

    private double _stress = 0.2;
    private double _arousal = 0.2;
    private double _fatigue = 0.1;
    private Stimulus? _lastStimulus;

    public Psyche(Temperament? temperament = null, ExpressionStyle? style = null) {
        _temperament = temperament ?? new Temperament();
        _style = style ?? new ExpressionStyle();

        foreach (var key in PsycheModel.EmotionKeys) _emotion[key] = _temperament.BaselineEmotion.GetValueOrDefault(key);
        foreach (var key in PsycheModel.MoodKeys) _mood[key] = _temperament.BaselineMood.GetValueOrDefault(key);
        foreach (var key in PsycheModel.DriveKeys) _drive[key] = _temperament.BaselineDrive.GetValueOrDefault(key);
    }

    // Tick is intentionally removed; progression is driven by scene steps or explicit time-passed stimuli.
    public void ApplySceneStep(SceneStep step) {
        if (step.Elapsed <= TimeSpan.Zero) {
            RebuildEffectiveRegulation();
            return;
        }

        ApplyTimePassed(step.Elapsed);

        if (step.DecayMode == SceneDecayMode.HardResetRecentFlags) {
            foreach (var key in PsycheModel.RecentFlagKeys) {
                _recentFlags[key] = 0;
            }
        }
    }

    public void ApplyStimulus(Stimulus s, IAffinityView affinity) {
        _lastStimulus = s;

        if (s.Tags.Contains("time_passed") && s.TimePassed is { } dt && dt > TimeSpan.Zero) {
            ApplyTimePassed(dt);
            return;
        }

        var bias = affinity.InterpretationBias(s.ActorId);
        var trust = affinity.Trust(s.ActorId);
        var safety = affinity.SafetyWith(s.ActorId);
        var sexualAwareness = affinity.SexualAwareness(s.ActorId);

        var effectiveValence = PsycheMath.ClampSigned(s.Valence + bias);
        var effectiveIntensity = PsycheMath.Clamp01(s.Intensity * (0.75 + 0.5 * (1 - s.Ambiguity)));
        var effectiveViolation = PsycheMath.Clamp01(s.ViolationLevel * (1 - trust * 0.5));

        var tagSensitivity = s.Tags.Count == 0 ? 1.0 : s.Tags.Average(tag => _temperament.SensitivityToTag.GetValueOrDefault(tag, 1));
        var i = effectiveIntensity * tagSensitivity;

        RebuildEffectiveRegulation();

        foreach (var tag in s.Tags) {
            switch (tag) {
                case "praise":
                case "admiration":
                case "thanks":
                    AddEmotion("joy", 0.6 * i);
                    AddEmotion("affection", 0.3 * i);
                    AddMood("confidence", 0.2 * i);
                    SetRecent("recent_praise", 0.7 * i);
                    break;
                case "insult":
                case "ridicule":
                    AddEmotion("anger", 0.6 * i * (1 - _regulationEffective["self_control"] * 0.3));
                    AddEmotion("shame", 0.4 * i * s.SocialCost);
                    AddEmotion("sadness", 0.3 * i);
                    AddMood("irritability", 0.2 * i);
                    break;
                case "humiliation":
                    AddEmotion("shame", 0.8 * i);
                    AddEmotion("anger", 0.4 * i * _regulationEffective["pride"]);
                    AddStress(0.5 * i);
                    SetRecent("recent_humiliation", 0.8 * i);
                    break;
                case "threat":
                case "violence":
                case "danger":
                    AddEmotion("fear", 0.8 * i);
                    AddStress(0.7 * i);
                    AddArousal(0.4 * i);
                    SetRecent("recent_threat", 0.9 * i);
                    break;
                case "comfort":
                case "support":
                case "protection":
                case "safe_signal":
                    AddEmotion("joy", 0.3 * i);
                    AddEmotion("fear", -0.3 * i);
                    AddStress(-0.4 * i);
                    AddMood("rested", 0.2 * i);
                    AddDrive("belonging_need", -0.2 * i);
                    break;
                case "rejection":
                case "neglect":
                    AddEmotion("sadness", 0.6 * i);
                    AddEmotion("anger", 0.3 * i);
                    AddMood("loneliness", 0.5 * i);
                    SetRecent("recent_rejection", 0.8 * i);
                    break;
                case "betrayal":
                    AddEmotion("anger", 0.7 * i);
                    AddEmotion("sadness", 0.5 * i);
                    AddStress(0.6 * i);
                    AddEmotion("contempt", 0.3 * i);
                    break;
                case "touch":
                    if (safety >= 0.5) AddEmotion("affection", 0.4 * i);
                    else {
                        AddEmotion("disgust", 0.3 * i);
                        AddStress(0.3 * i);
                    }
                    break;
                case "intimate_touch":
                case "sexual":
                case "flirt":
                    AddEmotion("arousal_emotion", 0.7 * i * sexualAwareness);
                    AddEmotion("shame", 0.4 * i * s.SocialCost);
                    AddDrive("sexual_drive", 0.2 * i);
                    if (effectiveViolation > 0.3) {
                        AddEmotion("disgust", 0.6 * i);
                        AddEmotion("fear", 0.4 * i);
                        AddEmotion("anger", 0.4 * i);
                        SetRecent("recent_boundary_violation", effectiveViolation);
                    }
                    SetRecent("recent_intimacy", 0.6 * i);
                    break;
                case "gift":
                case "money":
                case "food":
                    AddEmotion("joy", 0.3 * i);
                    AddDrive("approval_need", -0.2 * i);
                    if (tag == "food") AddDrive("hunger", -0.4 * i);
                    break;
                case "status_loss":
                    AddDrive("status_need", 0.4 * i);
                    break;
                case "status_gain":
                    AddDrive("status_need", -0.3 * i);
                    AddMood("confidence", 0.2 * i);
                    break;
                case "boundary_crossing":
                case "coercion":
                    SetRecent("recent_boundary_violation", 0.8 * i);
                    AddEmotion("fear", 0.2 * i);
                    AddEmotion("disgust", 0.3 * i);
                    break;
            }
        }

        if (effectiveValence > 0.4) {
            AddMood("good_mood", 0.1 * i * effectiveValence);
        }
        else if (effectiveValence < -0.4) {
            AddMood("melancholy", 0.1 * i * Math.Abs(effectiveValence));
            AddMood("anxiety", 0.08 * i * Math.Abs(effectiveValence));
        }

        if (s.Tags.Contains("humiliation")) {
            AddDrive("autonomy_need", 0.2 * i);
            AddDrive("rebellion_drive", 0.3 * i);
        }

        ApplyCouplingRules();
    }

    public IReadOnlyDictionary<string, double> GenerateImpulses(string actorId, IAffinityView affinity, WorldContext? ctx = null) {
        var context = ctx ?? new WorldContext([]);
        RebuildEffectiveRegulation();

        var trust = affinity.Trust(actorId);
        var safety = affinity.SafetyWith(actorId);
        var relationBoost = 1 + (trust - 0.5) * 0.2;
        var threatBoost = 1 + (0.5 - safety) * 0.25;
        var isPublic = context.Tags.Contains("public");

        var contributions = new Dictionary<string, List<(string key, double value)>>();

        double Compose(string impulse, params (string key, double value)[] terms) {
            var sum = terms.Sum(x => x.value);
            contributions[impulse] = terms.OrderByDescending(t => Math.Abs(t.value)).Take(3).ToList();
            return PsycheMath.Clamp01(sum);
        }

        var violationMemoryProxy = _recentFlags["recent_boundary_violation"];

        var impulses = new Dictionary<string, double> {
            ["withdraw"] = Compose("withdraw",
                ("fear", 0.4 * _emotion["fear"] * threatBoost), ("disgust", 0.4 * _emotion["disgust"]), ("stress", 0.3 * _stress),
                ("fatigue", 0.3 * _fatigue), ("melancholy", 0.2 * _mood["melancholy"]), ("good_mood", -0.2 * _mood["good_mood"])),
            ["talk"] = Compose("talk",
                ("affection", 0.4 * _emotion["affection"] * relationBoost), ("joy", 0.3 * _emotion["joy"]), ("approval_need", 0.3 * _drive["approval_need"]),
                ("belonging_need", 0.2 * _drive["belonging_need"]), ("fear", -0.3 * _emotion["fear"]), ("shame", -0.2 * _emotion["shame"])),
            ["defy"] = Compose("defy",
                ("rebellion_drive", 0.5 * _drive["rebellion_drive"]), ("anger", 0.3 * _emotion["anger"]),
                ("autonomy_need", 0.2 * _drive["autonomy_need"]), ("stress", 0.2 * _stress),
                ("norm_adherence", -0.4 * _regulationEffective["norm_adherence"]), ("risk_aversion", -0.2 * _regulationEffective["risk_aversion"])),
            ["submit"] = Compose("submit",
                ("fear", 0.4 * _emotion["fear"]), ("risk_aversion", 0.3 * _regulationEffective["risk_aversion"]),
                ("norm_adherence", 0.2 * _regulationEffective["norm_adherence"]), ("anger", -0.3 * _emotion["anger"]), ("pride", -0.2 * _regulationEffective["pride"])),
            ["seek_touch"] = Compose("seek_touch",
                ("intimacy_need", 0.5 * _drive["intimacy_need"]), ("affection", 0.4 * _emotion["affection"] * relationBoost),
                ("arousal_emotion", 0.4 * _emotion["arousal_emotion"]), ("sexual_drive", 0.3 * _drive["sexual_drive"]),
                ("shame", -0.3 * _emotion["shame"]), ("fear", -0.2 * _emotion["fear"])),
            ["flirt"] = Compose("flirt",
                ("sexual_drive", 0.4 * _drive["sexual_drive"]), ("confidence", 0.4 * _mood["confidence"]),
                ("good_mood", 0.3 * _mood["good_mood"]), ("control_need", 0.2 * _drive["control_need"]),
                ("shyness", -0.4 * _regulationEffective["shyness"]), ("norm_adherence", -0.2 * _regulationEffective["norm_adherence"])),
            ["avoid_touch"] = Compose("avoid_touch",
                ("disgust", 0.6 * _emotion["disgust"]), ("fear", 0.4 * _emotion["fear"] * threatBoost), ("violation_memory_proxy", 0.4 * violationMemoryProxy),
                ("stress", 0.3 * _stress), ("affection", -0.2 * _emotion["affection"])),
            ["lie"] = Compose("lie",
                ("shame", 0.4 * _emotion["shame"]), ("fear", 0.3 * _emotion["fear"]), ("status_need", 0.2 * _drive["status_need"]),
                ("empathy", -0.3 * _regulationEffective["empathy"]), ("norm_adherence", -0.2 * _regulationEffective["norm_adherence"])),
            ["hide"] = Compose("hide",
                ("shame", 0.4 * _emotion["shame"]), ("stress", 0.3 * _stress), ("anxiety", 0.2 * _mood["anxiety"]),
                ("confidence", -0.2 * _mood["confidence"])),
            ["attack_verbal"] = Compose("attack_verbal",
                ("anger", 0.6 * _emotion["anger"]), ("contempt", 0.3 * _emotion["contempt"]), ("aggression_drive", 0.2 * _drive["aggression_drive"]),
                ("stress", 0.2 * _stress), ("self_control", -0.4 * _regulationEffective["self_control"]), ("empathy", -0.2 * _regulationEffective["empathy"])),
            ["attack_physical"] = Compose("attack_physical", ("anger", 0.7 * _emotion["anger"]), ("fear", -0.15 * _emotion["fear"])),
            ["help"] = Compose("help", ("affection", 0.45 * _emotion["affection"] * relationBoost), ("empathy", 0.4 * _regulationEffective["empathy"]), ("fatigue", -0.2 * _fatigue)),
            ["please"] = Compose("please", ("approval_need", 0.45 * _drive["approval_need"]), ("fear", 0.25 * _emotion["fear"]), ("pride", -0.2 * _regulationEffective["pride"])),
            ["assert"] = Compose("assert", ("confidence", 0.45 * _mood["confidence"]), ("control_need", 0.25 * _drive["control_need"]), ("fear", -0.2 * _emotion["fear"])),
            ["eat"] = Compose("eat", ("hunger", 0.8 * _drive["hunger"]), ("fatigue", 0.1 * _fatigue)),
            ["sleep"] = Compose("sleep", ("sleepiness", 0.75 * _drive["sleepiness"]), ("fatigue", 0.35 * _fatigue), ("arousal", -0.2 * _arousal)),
            ["seek_safety"] = Compose("seek_safety", ("fear", 0.6 * _emotion["fear"]), ("safety_need", 0.45 * _drive["safety_need"]), ("stress", 0.3 * _stress))
        };

        if (_emotion["shame"] > 0.65 && _regulationEffective["pride"] > 0.6) impulses["attack_verbal"] = PsycheMath.Clamp01(impulses["attack_verbal"] + 0.15);
        if (_emotion["fear"] > 0.75 && _arousal > 0.6) impulses["defy"] = PsycheMath.Clamp01(impulses["defy"] + 0.1);
        if (_mood["loneliness"] > 0.7 && _recentFlags["recent_rejection"] > 0.3) impulses["seek_touch"] = PsycheMath.Clamp01(impulses["seek_touch"] + 0.15);
        if (_stress > 0.85) impulses["lie"] = PsycheMath.Clamp01(impulses["lie"] + 0.1);
        if (isPublic) impulses["flirt"] = PsycheMath.Clamp01(impulses["flirt"] * 0.75);

        _lastImpulseContributions.Clear();
        foreach (var kvp in contributions) {
            _lastImpulseContributions[kvp.Key] = string.Join(" + ", kvp.Value.Select(v => $"{v.key}({v.value:F2})"));
        }

        return impulses;
    }

    public ExpressionHints GenerateExpressionHints(string actorId, IAffinityView affinity, IReadOnlyDictionary<string, double> impulses, WorldContext? ctx = null) {
        var context = ctx ?? new WorldContext([]);
        var isPublic = context.Tags.Contains("public");
        var trust = affinity.Trust(actorId);

        var talkProbability = PsycheMath.Clamp01(impulses["talk"] - impulses["withdraw"] * 0.8 - _fatigue * 0.4 - _emotion["fear"] * 0.3 + trust * 0.1);
        var endConversationProbability = PsycheMath.Clamp01(impulses["withdraw"] * 0.7 + _mood["irritability"] * 0.3 + _fatigue * 0.4);
        var silence = PsycheMath.Clamp01(_emotion["shame"] * 0.5 + _mood["melancholy"] * 0.4 + _fatigue * 0.3 + (isPublic ? 0.05 : 0));

        var tone = "neutral";
        if (_emotion["anger"] > 0.6) tone = _style.AggressionMasking > 0.6 ? "cold" : "hostile";
        else if (_emotion["shame"] > 0.5) tone = _style.ShameMasking > 0.6 ? "playful" : "shy";
        else if (_fatigue > 0.7) tone = "tired";
        else if (_emotion["affection"] + _emotion["joy"] > 0.8) tone = "warm";

        var distance = impulses["seek_touch"] > impulses["avoid_touch"] + 0.2 ? "close" : impulses["withdraw"] > 0.5 ? "far" : "normal";
        var gaze = impulses["attack_verbal"] > 0.6 ? "glare" : _emotion["shame"] > 0.5 ? "avoid_eye_contact" : "seek_eye_contact";
        var speechRate = _arousal > 0.7 ? "fast" : _fatigue > 0.6 ? "slow" : "normal";
        var volume = _emotion["anger"] > 0.6 ? "loud" : _emotion["shame"] > 0.5 ? "quiet" : "normal";
        var politeness = PsycheMath.Clamp01(_style.PolitenessBaseline + 0.2 * _regulationEffective["norm_adherence"] - 0.2 * impulses["attack_verbal"] + (isPublic ? 0.05 : 0));

        var microActions = new List<string>();
        if (_emotion["anger"] > 0.5) microActions.Add("arms_crossed");
        if (_emotion["shame"] > 0.4) microActions.Add("avert_gaze");
        if (_fatigue > 0.5) microActions.Add("sigh");
        if (_emotion["joy"] > 0.4) microActions.Add("smile");

        return new ExpressionHints(talkProbability, endConversationProbability, tone, distance, gaze, speechRate, politeness, volume, silence, microActions);
    }

    public PsycheSnapshot GetSnapshot() => new(
        new Dictionary<string, double>(_emotion),
        new Dictionary<string, double>(_mood),
        new Dictionary<string, double>(_drive),
        new Dictionary<string, double>(_regulationBase),
        new Dictionary<string, double>(_regulationEffective),
        new Dictionary<string, double>(_recentFlags),
        _stress,
        _arousal,
        _fatigue,
        _lastStimulus is null ? null : $"{_lastStimulus.Value.Id} tags=[{string.Join(',', _lastStimulus.Value.Tags)}] intensity={_lastStimulus.Value.Intensity:F2}",
        new Dictionary<string, string>(_lastImpulseContributions)
    );

    private void ApplyTimePassed(TimeSpan dt) {
        foreach (var k in PsycheModel.EmotionKeys) {
            _emotion[k] = PsycheMath.ApproachHalfLife(_emotion[k], _temperament.BaselineEmotion.GetValueOrDefault(k), _temperament.EmotionHalfLife[k], dt);
        }

        foreach (var k in PsycheModel.MoodKeys) {
            _mood[k] = PsycheMath.ApproachHalfLife(_mood[k], _temperament.BaselineMood.GetValueOrDefault(k), _temperament.MoodHalfLife[k], dt);
        }

        foreach (var k in PsycheModel.DriveKeys) {
            var rate = _temperament.DriveAccumulationPerSecond.GetValueOrDefault(k);
            var gain = _temperament.DriveGain.GetValueOrDefault(k, 1);
            _drive[k] = PsycheMath.Clamp01(_drive[k] + (rate * dt.TotalSeconds * gain));
        }

        _stress = PsycheMath.ApproachHalfLife(_stress, 0.2, _temperament.StressHalfLife, dt);
        _arousal = PsycheMath.ApproachHalfLife(_arousal, 0.15, _temperament.ArousalHalfLife, dt);
        _fatigue = PsycheMath.Clamp01(_fatigue + (dt.TotalSeconds / TimeSpan.FromHours(16).TotalSeconds));

        foreach (var k in PsycheModel.RecentFlagKeys) {
            _recentFlags[k] = PsycheMath.ApproachHalfLife(_recentFlags[k], 0, _temperament.RecentFlagHalfLife[k], dt);
        }

        ApplyCouplingRules();
    }

    private void RebuildEffectiveRegulation() {
        foreach (var key in PsycheModel.RegulationKeys) {
            _regulationEffective[key] = _regulationBase[key];
        }

        if (_stress > 0.8) _regulationEffective["self_control"] = PsycheMath.Clamp01(_regulationEffective["self_control"] * 0.7);
        if (_fatigue > 0.7) _regulationEffective["self_control"] = PsycheMath.Clamp01(_regulationEffective["self_control"] * 0.85);
    }

    private void AddEmotion(string key, double delta) => _emotion[key] = PsycheMath.Clamp01(_emotion[key] + delta * _temperament.EmotionGain.GetValueOrDefault(key, 1));
    private void AddMood(string key, double delta) => _mood[key] = PsycheMath.Clamp01(_mood[key] + delta * _temperament.MoodGain.GetValueOrDefault(key, 1));
    private void AddDrive(string key, double delta) => _drive[key] = PsycheMath.Clamp01(_drive[key] + delta * _temperament.DriveGain.GetValueOrDefault(key, 1));
    private void AddStress(double delta) => _stress = PsycheMath.Clamp01(_stress + delta);
    private void AddArousal(double delta) => _arousal = PsycheMath.Clamp01(_arousal + delta);
    private void SetRecent(string key, double value) => _recentFlags[key] = PsycheMath.Clamp01(Math.Max(_recentFlags[key], value));

    private void ApplyCouplingRules() {
        RebuildEffectiveRegulation();
        if (_emotion["shame"] > 0.6 && _regulationEffective["pride"] > 0.6) AddEmotion("anger", 0.25 * (_emotion["shame"] - 0.6));
        if (_emotion["fear"] > 0.7) AddDrive("aggression_drive", 0.1 * (_emotion["fear"] - 0.7));
        if (_mood["loneliness"] > 0.7) AddDrive("intimacy_need", 0.1 * (_mood["loneliness"] - 0.7));
        if (_arousal < 0.25 && _emotion["arousal_emotion"] > 0.5) AddEmotion("guilt", 0.08);
    }
}

public readonly record struct ExpressionHints(
    double TalkProbability,
    double EndConversationProbability,
    string Tone,
    string Distance,
    string Gaze,
    string SpeechRate,
    double Politeness,
    string Volume,
    double Silence,
    IReadOnlyCollection<string> MicroActions
);

public readonly record struct PsycheSnapshot(
    IReadOnlyDictionary<string, double> Emotion,
    IReadOnlyDictionary<string, double> Mood,
    IReadOnlyDictionary<string, double> Drive,
    IReadOnlyDictionary<string, double> RegulationBase,
    IReadOnlyDictionary<string, double> RegulationEffective,
    IReadOnlyDictionary<string, double> RecentFlags,
    double Stress,
    double Arousal,
    double Fatigue,
    string? LastStimulusSummary,
    IReadOnlyDictionary<string, string> ImpulseContributionLogs
);

internal static class PsycheModel {
    public static readonly string[] EmotionKeys = ["joy", "anger", "fear", "sadness", "disgust", "surprise", "interest", "shame", "guilt", "contempt", "affection", "arousal_emotion"];
    public static readonly string[] MoodKeys = ["good_mood", "irritability", "anxiety", "melancholy", "confidence", "loneliness", "rested"];
    public static readonly string[] DriveKeys = ["hunger", "thirst", "sleepiness", "pain", "approval_need", "status_need", "belonging_need", "autonomy_need", "control_need", "curiosity_need", "safety_need", "sexual_drive", "intimacy_need", "rebellion_drive", "aggression_drive"];
    public static readonly string[] RegulationKeys = ["self_control", "norm_adherence", "empathy", "risk_aversion", "shyness", "pride", "jealousy_trait", "trauma_sensitivity"];
    public static readonly string[] RecentFlagKeys = ["recent_rejection", "recent_humiliation", "recent_boundary_violation", "recent_praise", "recent_threat", "recent_intimacy"];
    public static readonly string[] DefaultTags = ["praise", "admiration", "thanks", "insult", "ridicule", "humiliation", "rejection", "betrayal", "neglect", "comfort", "support", "protection", "threat", "violence", "dominance", "coercion", "touch", "intimate_touch", "sexual", "flirt", "gift", "money", "food", "status_gain", "status_loss", "boundary_crossing", "apology", "repair_attempt", "competition", "envy_trigger", "danger", "safe_signal", "time_passed"];

    public static Dictionary<string, TimeSpan> CreateDefaultEmotionHalfLife() => new() {
        ["surprise"] = TimeSpan.FromSeconds(15), ["fear"] = TimeSpan.FromSeconds(45), ["anger"] = TimeSpan.FromSeconds(60), ["joy"] = TimeSpan.FromSeconds(70),
        ["shame"] = TimeSpan.FromSeconds(90), ["guilt"] = TimeSpan.FromSeconds(120), ["sadness"] = TimeSpan.FromSeconds(80), ["disgust"] = TimeSpan.FromSeconds(80),
        ["interest"] = TimeSpan.FromSeconds(70), ["contempt"] = TimeSpan.FromSeconds(100), ["affection"] = TimeSpan.FromSeconds(75), ["arousal_emotion"] = TimeSpan.FromSeconds(80)
    };

    public static Dictionary<string, TimeSpan> CreateDefaultMoodHalfLife() => new() {
        ["irritability"] = TimeSpan.FromHours(6), ["anxiety"] = TimeSpan.FromHours(8), ["melancholy"] = TimeSpan.FromHours(12),
        ["good_mood"] = TimeSpan.FromHours(6), ["confidence"] = TimeSpan.FromHours(8), ["loneliness"] = TimeSpan.FromHours(12), ["rested"] = TimeSpan.FromHours(6)
    };

    public static Dictionary<string, double> CreateDefaultRegulation() => new() {
        ["self_control"] = 0.65, ["norm_adherence"] = 0.6, ["empathy"] = 0.6, ["risk_aversion"] = 0.55,
        ["shyness"] = 0.45, ["pride"] = 0.55, ["jealousy_trait"] = 0.35, ["trauma_sensitivity"] = 0.3
    };

    public static Dictionary<string, double> CreateDefaultDriveAccumulation() {
        static double perSec(double hoursToOne) => 1.0 / TimeSpan.FromHours(hoursToOne).TotalSeconds;
        return new Dictionary<string, double> {
            ["hunger"] = perSec(6), ["thirst"] = perSec(8), ["sleepiness"] = perSec(16), ["pain"] = perSec(72), ["approval_need"] = perSec(36),
            ["status_need"] = perSec(48), ["belonging_need"] = perSec(24), ["autonomy_need"] = perSec(24), ["control_need"] = perSec(24), ["curiosity_need"] = perSec(20),
            ["safety_need"] = perSec(30), ["sexual_drive"] = perSec(24), ["intimacy_need"] = perSec(18), ["rebellion_drive"] = perSec(30), ["aggression_drive"] = perSec(30)
        };
    }
}

internal static class PsycheMath {
    public static double Clamp01(double value) => Math.Max(0, Math.Min(1, value));
    public static double ClampSigned(double value) => Math.Max(-1, Math.Min(1, value));

    public static double ApproachHalfLife(double current, double target, TimeSpan halfLife, TimeSpan dt) {
        if (halfLife <= TimeSpan.Zero) return target;
        var lambda = Math.Pow(0.5, dt.TotalSeconds / halfLife.TotalSeconds);
        return Clamp01(target + ((current - target) * lambda));
    }

    public static Dictionary<string, T> KeysToValue<T>(IEnumerable<string> keys, T value) where T : notnull {
        var map = new Dictionary<string, T>();
        foreach (var k in keys) map[k] = value;
        return map;
    }
}
