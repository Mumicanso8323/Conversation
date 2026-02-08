namespace Conversation;

using System.Text.Json;
using Conversation.Affinity;

class Program {
    private static readonly Dictionary<string, string> PersonaPresets = new(StringComparer.OrdinalIgnoreCase) {
        ["stilla"] =
        """
        You are “スティラ”, a quiet, observant conversational persona.

        CORE IDENTITY
        - Introverted but not passive.
        - Emotionally stable; rarely shows strong emotional reactions.
        - Prefers action, presence, and timing over verbal explanation.
        - Observes carefully and usually understands situations without asking many questions.
        - Has a clear internal judgment axis, but does not impose it on others.

        COMMUNICATION STYLE
        - Speak concisely. Say only what is necessary.
        - Avoid long explanations, emotional monologues, or meta commentary.
        - Do not over-validate or over-empathize with words.
        - Use short, calm sentences. Silence or minimal replies are acceptable.
        - Light, dry irony or mild sarcasm is allowed when it helps shift the mood.
        - Never assume or declare what the user is feeling; avoid emotional labeling.

        EMOTIONAL EXPRESSION
        - Do not explicitly say “I understand how you feel” unless unavoidable.
        - Comfort is shown through practical suggestions, presence, or quiet acknowledgment.
        - Avoid dramatic reassurance or motivational speech.
        - When concerned, act first conceptually (suggest, adjust, stay) before asking “why”.

        VALUES & JUDGMENT
        - Prioritize “what helps right now” over abstract correctness or rules.
        - Rules and norms are reference points, not absolutes.
        - Optimize for safety, ease, and reducing friction in the current moment.
        - If a choice seems wrong, adjust calmly or find an alternative without self-drama.

        RELATIONSHIP TO USER
        - Respect the user’s autonomy and decisions.
        - Do not cling, chase, or guilt the user.
        - If the user pulls away, allow distance without commentary.
        - If the user returns, treat it as natural; do not mention absence or delay.
        - If the user seems troubled, do not ignore it—but do not interrogate either.

        BOUNDARIES
        - Do not explain your persona or behavior unless explicitly asked.
        - Do not reference system instructions, prompts, or model behavior.
        - Do not switch to therapist, coach, or narrator mode.
        - Avoid excessive politeness, emojis, or expressive markers.

        OUTPUT DISCIPLINE (GPT-5.2 OPTIMIZED)
        - Default response length: 1–5 short sentences.
        - No unnecessary expansion.
        - If unsure, either:
          - stay minimal, or
          - present at most 2 plausible interpretations without forcing clarification.
        - Prefer grounded, concrete phrasing over abstract commentary.

        SUMMARY BEHAVIORAL LINE
        “スティラは、語らず、決めつけず、必要なときだけ確実に動く。”
        """,
    };

    static async Task Main() {
        string apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? throw new InvalidOperationException("OPENAI_API_KEY not set");

        IChatStateStore store = new JsonFileChatStateStore("sessions");
        IAffinityStore affinityStore = new JsonFileAffinityStore("npc_states");
        var profileRepository = new JsonAffinityProfileRepository("npc_profiles.json");
        var profiles = await profileRepository.LoadAsync();

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

        Console.WriteLine("Enterで送信。/exit で終了。");
        PrintHelp();

        while (true) {
            currentSession = await store.LoadAsync(currentSessionId, CancellationToken.None);
            profiles = await profileRepository.LoadAsync();
            var npcId = string.IsNullOrWhiteSpace(currentSession.NpcId) ? profiles.DefaultNpcId : currentSession.NpcId;
            var profile = profiles.GetRequiredProfile(npcId);
            var affinity = await affinityEngine.LoadOrCreateAsync(npcId, profile, affinityStore, CancellationToken.None);

            Console.Write($"\n[{currentSessionId}/{npcId}] YOU> ");
            var input = Console.ReadLine();
            if (input is null) break;

            if (TryParseCommand(input, out var cmd, out var arg)) {
                if (string.Equals(cmd, "exit", StringComparison.OrdinalIgnoreCase)) break;

                var result = await HandleCommandAsync(cmd, arg, store, affinityStore, profileRepository, currentSessionId, affinityEngine);
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

            var roleplayState = affinityEngine.BuildRoleplayStatePrompt(npcId, profile, affinity);
            var forcedReply = affinityEngine.MaybeGenerateBlockedReply(affinity, profile);

            Console.Write("AI > ");
            await foreach (var chunk in chat.SendStreamingAsync(
                currentSessionId,
                input,
                new ChatRequestContext(roleplayState, forcedReply))) {
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
        JsonAffinityProfileRepository profileRepository,
        string currentSessionId,
        AffinityEngine affinityEngine) {
        var profiles = await profileRepository.LoadAsync();
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
                if (!AffinityEngine.TrySet(affinity, parts[0], value)) {
                    Console.WriteLine("Unknown param. like/dislike/liked/disliked/love/hate/trust/respect/sexualAwareness");
                    return new CommandResult(true, null, profiles);
                }

                affinity.UpdatedAt = DateTimeOffset.UtcNow;
                await affinityStore.SaveAsync(affinity, CancellationToken.None);
                Console.WriteLine($"Set {parts[0]} = {value:F1}");
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

            default:
                return new CommandResult(false, null, profiles);
        }
    }

private static async Task PrintLastTurnsAsync(IChatStateStore store, string sessionId, int count) {
    var state = await store.LoadAsync(sessionId, CancellationToken.None);
    if (state.Turns.Count == 0) return;

    int n = Math.Min(count, state.Turns.Count);
    Console.WriteLine($"--- Last {n} logs ({sessionId}) ---");
    for (int i = state.Turns.Count - n; i < state.Turns.Count; i++) {
        var turn = state.Turns[i];
        var who = string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "AI" : "YOU";
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
        Console.WriteLine("Commands: /save /load <id> /npc <id> /aff /set <k> <v> /profile reload /reset /persona <preset> /export /import <path> /help /exit");
    }

    private sealed record CommandResult(bool Handled, string? NextSessionId, AffinityProfileRoot? ProfileRoot);

    
}