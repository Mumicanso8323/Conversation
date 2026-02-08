namespace Conversation;

using System.IO;
using System.Text.Json;
using Conversation.Affinity;
using Conversation.Psyche;
using Conversation.Standee;

class Program {
    private const string AI = "Stella ";
    private const string YOU = "Ashwell";
    private static readonly Dictionary<string, string> PersonaPresets = new(StringComparer.OrdinalIgnoreCase) {
        ["stilla"] =
        """
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
        """,
    };

    [STAThread]
    static async Task Main() {
        string apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? throw new InvalidOperationException("OPENAI_API_KEY not set");

        IChatStateStore store = new JsonFileChatStateStore("sessions");
        IAffinityStore affinityStore = new JsonFileAffinityStore("npc_states");
        IPsycheStore psycheStore = new JsonFilePsycheStore("psyche_states");
        var profileRepository = new JsonAffinityProfileRepository("npc_profiles.json");
        var psycheProfileRepository = new JsonPsycheProfileRepository("psyche_profiles.json");
        var profiles = await profileRepository.LoadAsync();
        var psycheProfiles = await psycheProfileRepository.LoadAsync();

        var chat = new UniversalChatModule(
            new ChatModuleOptions(
                Model: "gpt-5.2",
                ApiKey: apiKey,
                SystemInstructions: PersonaPresets["stilla"],
                Mode: ChatEngineMode.ChatCompletions,
                Streaming: true
            ),
            store
        );

        var affinityEngine = new AffinityEngine("gpt-5.1", apiKey);
        var psycheOrchestrator = new PsycheOrchestrator();
        var stateNarrator = new StateNarrator();
        var psycheJudge = new PsycheJudge("gpt-5.1", apiKey);
        var standeeConfig = StandeeConfig.LoadOrDefault("standee_config.json");
        StandeeClient? standeeClient = null;
        StandeeJudge? standeeJudge = null;
        if (standeeConfig.Enabled) {
            standeeClient = new StandeeClient(standeeConfig);
            standeeJudge = new StandeeJudge("gpt-5.1", apiKey);
            await standeeClient.InitializeAsync(CancellationToken.None);
        }

        chat.AddChatFunctionTool(
            name: "roll_dice",
            description: "サイコロを振る。TRPG演出用。",
            jsonSchema: """
            {
              "type": "object",
              "properties": { "sides": { "type": "integer", "minimum": 2, "maximum": 100 } },
              "required": ["sides"]
            }
            """,
            handler: async (args, ct) => {
                int sides = args.GetProperty("sides").GetInt32();
                int v = Random.Shared.Next(1, sides + 1);
                await Task.Yield();
                return v.ToString();
            }
        );

        string currentSessionId = "stilla";

        // 起動時: 前回までのログのラスト5件を表示
        await PrintLastTurnsAsync(store, currentSessionId, 5);

        var currentSession = await store.LoadAsync(currentSessionId, CancellationToken.None);
        if (string.IsNullOrWhiteSpace(currentSession.NpcId)) {
            currentSession.NpcId = profiles.DefaultNpcId;
            await store.SaveAsync(currentSession, CancellationToken.None);
        }

        // PrintHelp();

        while (true) {
            currentSession = await store.LoadAsync(currentSessionId, CancellationToken.None);
            profiles = await profileRepository.LoadAsync();
            psycheProfiles = await psycheProfileRepository.LoadAsync();
            var npcId = string.IsNullOrWhiteSpace(currentSession.NpcId) ? profiles.DefaultNpcId : currentSession.NpcId;
            var profile = profiles.GetRequiredProfile(npcId);
            var psycheProfile = psycheProfiles.GetRequiredProfile(npcId);
            var affinity = await affinityEngine.LoadOrCreateAsync(npcId, profile, affinityStore, CancellationToken.None);
            var psyche = await psycheOrchestrator.LoadOrCreateAsync(npcId, psycheProfile, psycheStore, CancellationToken.None);

            Console.Write($"\n{YOU}> ");
            var input = Console.ReadLine();
            if (input is null) break;

            if (TryParseCommand(input, out var cmd, out var arg)) {
                if (string.Equals(cmd, "exit", StringComparison.OrdinalIgnoreCase)) break;

                var result = await HandleCommandAsync(cmd, arg, store, affinityStore, psycheStore, profileRepository, psycheProfileRepository, currentSessionId, affinityEngine, psycheOrchestrator);
                if (result.NextSessionId is not null) {
                    currentSessionId = result.NextSessionId;
                }

                if (result.ProfileRoot is not null) {
                    profiles = result.ProfileRoot;
                }

                if (!result.Handled) {
                    Console.WriteLine($"Unknown command: /{cmd}");
                }

                currentSession = await store.LoadAsync(currentSessionId, CancellationToken.None);
                npcId = string.IsNullOrWhiteSpace(currentSession.NpcId) ? profiles.DefaultNpcId : currentSession.NpcId;
                profile = profiles.GetRequiredProfile(npcId);
                affinity = await affinityEngine.LoadOrCreateAsync(npcId, profile, affinityStore, CancellationToken.None);
                continue;
            }

            var delta = await affinityEngine.EvaluateDeltaAsync(input, profile.DisplayName, CancellationToken.None);
            affinityEngine.ApplyDelta(affinity, profile, delta);
            await affinityStore.SaveAsync(affinity, CancellationToken.None);

            var recentContext = BuildRecentContext(currentSession.Turns, 6);
            var narrated = stateNarrator.BuildJudgeStateText(npcId, psycheProfile, affinity, psyche, input, recentContext);
            var judged = await psycheJudge.EvaluateAsync(input, profile.DisplayName, recentContext, narrated, CancellationToken.None);

            if (standeeJudge is not null && standeeClient is not null) {
                var sprite = await standeeJudge.EvaluateAsync(input, profile.DisplayName, recentContext, narrated, CancellationToken.None);
                await standeeClient.SetSpriteAsync(sprite, CancellationToken.None);
            }

            psycheOrchestrator.ApplyDelta(psyche, psycheProfile, judged.PsycheDelta);
            await psycheStore.SaveAsync(psyche, CancellationToken.None);

            var roleplayState = affinityEngine.BuildRoleplayStatePrompt(npcId, profile, affinity);
            var forcedReply = affinityEngine.MaybeGenerateBlockedReply(affinity, profile);
            var additionalSystem = $"{roleplayState}\n\n{judged.Directives.ToSystemBlock()}";

            Console.Write($"{AI}> ");
            await foreach (var chunk in chat.SendStreamingAsync(
                currentSessionId,
                input,
                new ChatRequestContext(additionalSystem, forcedReply))) {
                foreach (var ch in chunk) Console.Write(ch);
            }

            Console.WriteLine(string.Empty);
        }
    }

    private static async Task<CommandResult> HandleCommandAsync(
        string cmd,
        string arg,
        IChatStateStore store,
        IAffinityStore affinityStore,
        IPsycheStore psycheStore,
        JsonAffinityProfileRepository profileRepository,
        JsonPsycheProfileRepository psycheProfileRepository,
        string currentSessionId,
        AffinityEngine affinityEngine,
        PsycheOrchestrator psycheOrchestrator) {
        var profiles = await profileRepository.LoadAsync();
        var psycheProfiles = await psycheProfileRepository.LoadAsync();
        switch (cmd.ToLowerInvariant()) {
            case "help":
                PrintHelp();
                return new CommandResult(true, null, profiles);

            case "save": {
                var state = await store.LoadAsync(currentSessionId, CancellationToken.None);
                await store.SaveAsync(state, CancellationToken.None);
                Console.WriteLine($"Saved session: {state.SessionId}");
                return new CommandResult(true, null, profiles);
            }

            case "load": {
                if (string.IsNullOrWhiteSpace(arg)) {
                    Console.WriteLine("Usage: /load <sessionId>");
                    return new CommandResult(true, null, profiles);
                }

                var loaded = await store.LoadAsync(arg.Trim(), CancellationToken.None);
                if (string.IsNullOrWhiteSpace(loaded.NpcId)) {
                    loaded.NpcId = profiles.DefaultNpcId;
                    await store.SaveAsync(loaded, CancellationToken.None);
                }

                Console.WriteLine($"Loaded session: {loaded.SessionId}");
                return new CommandResult(true, loaded.SessionId, profiles);
            }

            case "npc": {
                if (string.IsNullOrWhiteSpace(arg)) {
                    Console.WriteLine("Usage: /npc <id>");
                    return new CommandResult(true, null, profiles);
                }

                var state = await store.LoadAsync(currentSessionId, CancellationToken.None);
                state.NpcId = arg.Trim();
                await store.SaveAsync(state, CancellationToken.None);
                var profile = profiles.GetRequiredProfile(state.NpcId);
                await affinityEngine.LoadOrCreateAsync(state.NpcId, profile, affinityStore, CancellationToken.None);
                Console.WriteLine($"NPC switched: {state.NpcId}");
                return new CommandResult(true, null, profiles);
            }

            case "aff": {
                var state = await store.LoadAsync(currentSessionId, CancellationToken.None);
                var npcId = string.IsNullOrWhiteSpace(state.NpcId) ? profiles.DefaultNpcId : state.NpcId;
                var profile = profiles.GetRequiredProfile(npcId);
                var affinity = await affinityEngine.LoadOrCreateAsync(npcId, profile, affinityStore, CancellationToken.None);
                Console.WriteLine(JsonSerializer.Serialize(affinity, new JsonSerializerOptions { WriteIndented = true }));
                return new CommandResult(true, null, profiles);
            }

            case "set": {
                var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length != 2 || !double.TryParse(parts[1], out var value)) {
                    Console.WriteLine("Usage: /set <param> <value>");
                    return new CommandResult(true, null, profiles);
                }

                var session = await store.LoadAsync(currentSessionId, CancellationToken.None);
                var npcId = string.IsNullOrWhiteSpace(session.NpcId) ? profiles.DefaultNpcId : session.NpcId;
                var profile = profiles.GetRequiredProfile(npcId);
                var affinity = await affinityEngine.LoadOrCreateAsync(npcId, profile, affinityStore, CancellationToken.None);
                if (AffinityEngine.TrySet(affinity, parts[0], value)) {
                    affinity.UpdatedAt = DateTimeOffset.UtcNow;
                    await affinityStore.SaveAsync(affinity, CancellationToken.None);
                    Console.WriteLine($"Set {parts[0]} = {value:F1}");
                    return new CommandResult(true, null, profiles);
                }

                var psycheProfile = psycheProfiles.GetRequiredProfile(npcId);
                var psyche = await psycheOrchestrator.LoadOrCreateAsync(npcId, psycheProfile, psycheStore, CancellationToken.None);
                if (!PsycheSetter.TrySet(psyche, parts[0], value, out _)) {
                    Console.WriteLine("Unknown param. affinity: like/dislike/liked/disliked/love/hate/trust/respect/sexualAwareness psyche: desire.<axis>.trait|deficit|gain libido.<axis>.trait|deficit|gain mood.current.valence|arousal|control mood.baseline.valence|arousal|control");
                    return new CommandResult(true, null, profiles);
                }

                await psycheStore.SaveAsync(psyche, CancellationToken.None);
                Console.WriteLine($"Set {parts[0]} = {value:F1}");
                return new CommandResult(true, null, profiles);
            }

            case "psy": {
                var state = await store.LoadAsync(currentSessionId, CancellationToken.None);
                var npcId = string.IsNullOrWhiteSpace(state.NpcId) ? profiles.DefaultNpcId : state.NpcId;
                var profile = profiles.GetRequiredProfile(npcId);
                var psycheProfile = psycheProfiles.GetRequiredProfile(npcId);
                var psyche = await psycheOrchestrator.LoadOrCreateAsync(npcId, psycheProfile, psycheStore, CancellationToken.None);
                var affinity = await affinityEngine.LoadOrCreateAsync(npcId, profile, affinityStore, CancellationToken.None);

                var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 0) {
                    PrintPsycheSummary(npcId, psycheProfile, affinity, psyche, BuildRecentContext(state.Turns, 6));
                    return new CommandResult(true, null, profiles);
                }

                if (parts.Length == 1 && string.Equals(parts[0], "json", StringComparison.OrdinalIgnoreCase)) {
                    Console.WriteLine(JsonSerializer.Serialize(psyche, new JsonSerializerOptions { WriteIndented = true }));
                    return new CommandResult(true, null, profiles);
                }

                if (parts.Length == 1 && string.Equals(parts[0], "reset", StringComparison.OrdinalIgnoreCase)) {
                    await psycheStore.DeleteAsync(npcId, CancellationToken.None);
                    psyche = await psycheOrchestrator.LoadOrCreateAsync(npcId, psycheProfile, psycheStore, CancellationToken.None);
                    Console.WriteLine($"Psyche reset: {npcId}");
                    PrintPsycheSummary(npcId, psycheProfile, affinity, psyche, BuildRecentContext(state.Turns, 6));
                    return new CommandResult(true, null, profiles);
                }

                if (parts.Length == 3 && string.Equals(parts[0], "set", StringComparison.OrdinalIgnoreCase) && double.TryParse(parts[2], out var value)) {
                    if (!PsycheSetter.TrySet(psyche, parts[1], value, out var error)) {
                        Console.WriteLine($"/psy set failed: {error}");
                        Console.WriteLine("Usage: /psy set <param> <value>");
                        return new CommandResult(true, null, profiles);
                    }

                    await psycheStore.SaveAsync(psyche, CancellationToken.None);
                    Console.WriteLine($"Set {parts[1]} = {value:F1}");
                    return new CommandResult(true, null, profiles);
                }

                Console.WriteLine("Usage: /psy [/json|reset|set <k> <v>]");
                return new CommandResult(true, null, profiles);
            }

            case "profile": {
                var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length > 0 && string.Equals(parts[0], "reload", StringComparison.OrdinalIgnoreCase)) {
                    var reloaded = await profileRepository.LoadAsync();
                    Console.WriteLine("Profile reloaded.");
                    return new CommandResult(true, null, reloaded);
                }

                Console.WriteLine("Usage: /profile reload");
                return new CommandResult(true, null, profiles);
            }

            case "reset": {
                var state = await store.LoadAsync(currentSessionId, CancellationToken.None);
                state.Turns.Clear();
                state.SummaryMemory = string.Empty;
                state.PreviousResponseId = null;
                await store.SaveAsync(state, CancellationToken.None);
                Console.WriteLine($"Reset session: {currentSessionId}");
                return new CommandResult(true, null, profiles);
            }

            case "persona": {
                if (string.IsNullOrWhiteSpace(arg)) {
                    Console.WriteLine("Usage: /persona <stilla>");
                    return new CommandResult(true, null, profiles);
                }

                var key = arg.Trim();
                if (!PersonaPresets.TryGetValue(key, out var persona)) {
                    Console.WriteLine("Unknown persona. Available: stilla");
                    return new CommandResult(true, null, profiles);
                }

                var state = await store.LoadAsync(currentSessionId, CancellationToken.None);
                state.SystemInstructions = persona;
                await store.SaveAsync(state, CancellationToken.None);
                Console.WriteLine($"Persona set: {key}");
                return new CommandResult(true, null, profiles);
            }

            case "export": {
                var state = await store.LoadAsync(currentSessionId, CancellationToken.None);
                Directory.CreateDirectory("exports");
                var path = Path.Combine("exports", $"{currentSessionId}-{DateTime.UtcNow:yyyyMMddHHmmss}.json");
                var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(path, json);
                Console.WriteLine($"Exported: {path}");
                Console.WriteLine(json);
                return new CommandResult(true, null, profiles);
            }

            case "import": {
                var importPath = string.IsNullOrWhiteSpace(arg) ? "import.json" : arg.Trim();
                if (!File.Exists(importPath)) {
                    Console.WriteLine($"Import file not found: {importPath}");
                    Console.WriteLine("Usage: /import <path-to-json> (default: import.json)");
                    return new CommandResult(true, null, profiles);
                }

                var json = await File.ReadAllTextAsync(importPath);
                var imported = JsonSerializer.Deserialize<ChatSessionState>(json);
                if (imported is null) {
                    Console.WriteLine("Import failed: invalid JSON");
                    return new CommandResult(true, null, profiles);
                }

                imported = new ChatSessionState {
                    SessionId = currentSessionId,
                    PreviousResponseId = imported.PreviousResponseId,
                    Turns = imported.Turns ?? new List<ChatTurn>(),
                    SummaryMemory = imported.SummaryMemory ?? string.Empty,
                    SystemInstructions = imported.SystemInstructions,
                    NpcId = string.IsNullOrWhiteSpace(imported.NpcId) ? profiles.DefaultNpcId : imported.NpcId
                };

                await store.SaveAsync(imported, CancellationToken.None);
                Console.WriteLine($"Imported to session: {currentSessionId}");
                return new CommandResult(true, null, profiles);
            }

            case "clear": {
                Console.Clear();
                return new CommandResult(true, null, profiles);
            }

            default:
                return new CommandResult(false, null, profiles);
        }
    }

    private static void PrintPsycheSummary(string npcId, PsycheProfileConfig profile, AffinityState affinity, PsycheState psyche, string recentContext) {
        Console.WriteLine($"Psyche: {npcId}");
        Console.WriteLine($"Mood current   : valence={psyche.Mood.CurrentValence:F1}, arousal={psyche.Mood.CurrentArousal:F1}, control={psyche.Mood.CurrentControl:F1}");
        Console.WriteLine($"Mood baseline  : valence={psyche.Mood.BaselineValence:F1}, arousal={psyche.Mood.BaselineArousal:F1}, control={psyche.Mood.BaselineControl:F1}");

        var desireTop = Enum.GetValues<DesireAxis>()
            .Select(axis => new { Axis = axis, Effective = PsycheOrchestrator.Effective(GetOrZero(psyche.DesireTrait, axis), GetOrZero(psyche.DesireDeficit, axis)) })
            .OrderByDescending(x => x.Effective)
            .Take(profile.K.KDesire)
            .ToList();

        Console.WriteLine($"Desire top{profile.K.KDesire} (effective = trait + deficit):");
        foreach (var item in desireTop) {
            Console.WriteLine($"- {item.Axis}: {item.Effective:F1}");
        }

        var trustOk = affinity.Trust >= profile.LibidoGate.MinTrust;
        var hateOk = affinity.Hate < profile.LibidoGate.MaxHate;
        var keywordHit = profile.LibidoGate.SexualKeywords.Any(k => recentContext.Contains(k, StringComparison.OrdinalIgnoreCase));
        var gated = !StateNarrator.EvaluateLibidoGate(profile, affinity, string.Empty, recentContext);
        Console.WriteLine($"Libido gate: {(gated ? "gated" : "open")} (trust {affinity.Trust:F1}/{profile.LibidoGate.MinTrust:F1}, hate {affinity.Hate:F1}/{profile.LibidoGate.MaxHate:F1}, keywordHit={keywordHit}, trustOk={trustOk}, hateOk={hateOk})");
        if (!gated) {
            var libidoTop = Enum.GetValues<LibidoAxis>()
                .Select(axis => new { Axis = axis, Effective = PsycheOrchestrator.Effective(GetOrZero(psyche.Libido.Trait, axis), GetOrZero(psyche.Libido.Deficit, axis)) })
                .OrderByDescending(x => x.Effective)
                .Take(profile.K.KLibido)
                .ToList();

            Console.WriteLine($"Libido top{profile.K.KLibido} (effective = trait + deficit):");
            foreach (var item in libidoTop) {
                Console.WriteLine($"- {item.Axis}: {item.Effective:F1}");
            }
        }
    }

    private static double GetOrZero<T>(Dictionary<T, double> map, T key) where T : notnull => map.TryGetValue(key, out var value) ? value : 0;

private static async Task PrintLastTurnsAsync(IChatStateStore store, string sessionId, int count) {
    var state = await store.LoadAsync(sessionId, CancellationToken.None);
    if (state.Turns.Count == 0) return;

    int n = Math.Min(count, state.Turns.Count);
    for (int i = state.Turns.Count - n; i < state.Turns.Count; i++) {
        var turn = state.Turns[i];
        var who = string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? AI : YOU;
        Console.WriteLine($"{who} > {turn.Text}");
    }
    Console.WriteLine(new string('-', 32));
}

    private static bool TryParseCommand(string input, out string cmd, out string arg) {
        cmd = string.Empty;
        arg = string.Empty;

        if (string.IsNullOrWhiteSpace(input) || !input.StartsWith('/'))
            return false;

        var body = input[1..].Trim();
        if (body.Length == 0)
            return false;

        var split = body.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        cmd = split[0];
        arg = split.Length > 1 ? split[1] : string.Empty;
        return true;
    }

    private static void PrintHelp() {
        Console.WriteLine("Commands: /save /load <id> /npc <id> /aff /psy [json|reset|set <k> <v>] /set <k> <v> /profile reload /reset /persona <preset> /export /import <path> /help /exit");
    }

    private static string BuildRecentContext(IReadOnlyList<ChatTurn> turns, int maxTurns) {
        if (turns.Count == 0) return "(none)";
        var start = Math.Max(0, turns.Count - maxTurns);
        return string.Join("\n", turns.Skip(start).Select(t => $"{t.Role}: {Shorten(t.Text, 120)}"));
    }

    private static string Shorten(string text, int max) {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return text.Length <= max ? text : text[..max] + "…";
    }

    private sealed record CommandResult(bool Handled, string? NextSessionId, AffinityProfileRoot? ProfileRoot);

    
}
