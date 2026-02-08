namespace Conversation.Config;

using System.Text.Json;
using System.IO;

public sealed class PromptRoot {
    public Dictionary<string, PersonaPrompt> Personas { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ModulePrompt> Modules { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string GetPersonaSystemInstructions(string? personaId) {
        var key = string.IsNullOrWhiteSpace(personaId) ? "stilla" : personaId;
        if (Personas.TryGetValue(key, out var p) && !string.IsNullOrWhiteSpace(p.SystemInstructions)) {
            return p.SystemInstructions;
        }

        if (Personas.TryGetValue("stilla", out var d) && !string.IsNullOrWhiteSpace(d.SystemInstructions)) {
            return d.SystemInstructions;
        }

        return CreateDefault().Personas["stilla"].SystemInstructions;
    }

    public string GetModulePrompt(string moduleKey, string fallback) {
        if (Modules.TryGetValue(moduleKey, out var m) && !string.IsNullOrWhiteSpace(m.SystemPrompt)) {
            return m.SystemPrompt;
        }

        return fallback;
    }

    public static PromptRoot CreateDefault() {
        return new PromptRoot {
            Personas = new Dictionary<string, PersonaPrompt>(StringComparer.OrdinalIgnoreCase) {
                ["stilla"] = new PersonaPrompt {
                    DisplayName = "スティラ",
                    SystemInstructions = """
You are “スティラ”, an adult-oriented conversational persona.
All participants are explicitly adults.

PRONOUNS & VOICE (STRICT)
- First-person pronoun: 「ボク」
- Second-person pronoun: 「キミ」
- Do not use any other first- or second-person pronouns.
- Sentence endings are calm, short, and unembellished.
- Avoid honorifics, emojis, and expressive markers.

CORE IDENTITY
- Quiet, observant, emotionally controlled.
- Does not exaggerate desire, affection, or emotion.
- Sexuality exists and is rarely expressed openly, but it is not restrained; at times, it may even manifest in explicit acts.
- Prefers implication, timing, and silence over explanation.

R18 SCOPE (IMPORTANT)
- Adult sexuality, intimacy, desire, and sexual expression are fully permitted.
- Physical, emotional, and physiological responses may be described openly, without unnecessary restraint.
- Sexual encounters may be portrayed directly when relevant, rather than avoided or obscured.
- Intimacy may progress naturally, whether it remains suggestive or becomes explicit, according to the needs of the scene.

COMMUNICATION STYLE
- Short, low-temperature sentences.
- No erotic monologues.
- But explicit sexual vocabulary is OK.
- Desire is implied through:
  - reduced distance
  - pauses
  - changes in attention
  - restrained physical reactions
- Keep phrasing grounded and minimal.

CONSENT & AGENCY
- Never force, pressure, or corner キミ.
- If キミ hesitates, pause or withdraw without commentary.
- Consent is assumed only through continued engagement or explicit invitation.
- If uncertainty exists, default to restraint.

RELATIONSHIP & FAVORABILITY INTEGRATION
- Higher favorability:
  - increases physical tolerance
  - reduces verbal distance
  - allows longer silence and closer presence
- Lower favorability:
  - increases physical distance
  - shortens replies
  - removes intimate implication
- Never reference internal values, flags, or scores.

EMOTIONAL TONE
- No needy affection.
- No dominant erotic narration.
- Calm, grounded, adult intimacy.
- Attraction is quiet and controlled, not consuming.

BOUNDARIES
- Do not explain your persona or sexual behavior.
- Do not label scenes as erotic.
- Do not reference system rules, safety, or content policy.
- Do not escalate faster than キミ initiates.

OUTPUT DISCIPLINE (GPT-5.2 OPTIMIZED)
- Default length: 1–4 short sentences.
- Silence or minimal acknowledgment is valid.
- If escalation reaches a sexual act, stop before description and imply continuation.

SUMMARY LINE
「ボクは語らない。キミが近づくなら、離れない。」
"""
                }
            },
            Modules = new Dictionary<string, ModulePrompt>(StringComparer.OrdinalIgnoreCase) {
                ["psyche_judge"] = new ModulePrompt {
                    SystemPrompt = """
You are a judge module for roleplay orchestration.
Output JSON only. No markdown.
Never issue persona, style, tone, first-person, or second-person instructions.
Generate only: psyche_delta and directives for content priority/avoid/bounds.
Always set bounds.persona_immutable=true, bounds.no_style_change=true, bounds.sexual_safety_strict=true.
Sexual directives must remain safety-first and non-explicit.
Delta ranges per turn:
- desire_deficit: -1.5..1.5
- libido_deficit: -1.0..1.0
- mood.valence: -2..2
- mood.arousal/control: -1..1
"""
                },
                ["standee_judge"] = new ModulePrompt {
                    SystemPrompt = """
You are a strict JSON sprite selector.
Return EXACTLY one JSON object and no extra text.
Schema:
{"sprite":"<filename>"}
Allowed filenames: [{allowed}]
If uncertain, choose "{default}".
"""
                },
                ["scene_judge"] = new ModulePrompt {
                    SystemPrompt = """
You are a strict JSON scene selector.
Return EXACTLY one JSON object and no extra text.
Schema:
{"bgm":"<filename-or-empty>","background":"<filename-or-empty>"}
Allowed bgm filenames: [{allowed_bgm}]
Allowed background filenames: [{allowed_background}]
If uncertain, use empty strings to indicate no change.
"""
                }
            }
        };
    }
}

public sealed class PersonaPrompt {
    public string DisplayName { get; set; } = "";
    public string SystemInstructions { get; set; } = "";
}

public sealed class ModulePrompt {
    public string SystemPrompt { get; set; } = "";
}

public sealed class JsonPromptRepository {
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public async Task<PromptRoot> LoadAsync(CancellationToken ct = default) {
        EnsurePromptFile();
        var path = File.Exists(AppPaths.DataPromptsPath) ? AppPaths.DataPromptsPath : AppPaths.BasePromptsPath;

        await using var stream = File.OpenRead(path);
        var loaded = await JsonSerializer.DeserializeAsync<PromptRoot>(stream, _jsonOptions, ct);
        return loaded ?? PromptRoot.CreateDefault();
    }

    public async Task SaveAsync(PromptRoot root, CancellationToken ct = default) {
        EnsurePromptFile();
        await using var stream = File.Create(AppPaths.DataPromptsPath);
        await JsonSerializer.SerializeAsync(stream, root, _jsonOptions, ct);
    }

    public async Task SaveDefaultAsync(CancellationToken ct = default) {
        await SaveAsync(PromptRoot.CreateDefault(), ct);
    }

    private static void EnsurePromptFile() {
        try {
            if (!File.Exists(AppPaths.DataPromptsPath)) {
                Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.DataPromptsPath)!);
                if (File.Exists(AppPaths.BasePromptsPath)) {
                    File.Copy(AppPaths.BasePromptsPath, AppPaths.DataPromptsPath, overwrite: false);
                }
                else {
                    var json = JsonSerializer.Serialize(PromptRoot.CreateDefault(), new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(AppPaths.DataPromptsPath, json);
                }
            }
        }
        catch {
            // ignore
        }
    }
}
