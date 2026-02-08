namespace Conversation;

using System.Text.Json;

class Program {
    private static readonly Dictionary<string, string> PersonaPresets = new(StringComparer.OrdinalIgnoreCase) {
        ["stilla"] =
@"You are “スティラ”, a quiet, observant conversational persona.

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
",
    };

    static async Task Main() {
        string apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? throw new InvalidOperationException("OPENAI_API_KEY not set");

        IChatStateStore store = new JsonFileChatStateStore("sessions");
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

        Console.WriteLine("Enterで送信。/exit で終了。");
        PrintHelp();

        while (true) {
            Console.Write($"\n[{currentSessionId}] YOU> ");
            var input = Console.ReadLine();
            if (input is null) break;

            if (TryParseCommand(input, out var cmd, out var arg)) {
                if (string.Equals(cmd, "exit", StringComparison.OrdinalIgnoreCase)) break;

                var result = await HandleCommandAsync(cmd, arg, store, currentSessionId);
                if (result.NextSessionId is not null)
                    currentSessionId = result.NextSessionId;
                if (!result.Handled)
                    Console.WriteLine($"Unknown command: /{cmd}");
                continue;
            }

            Console.Write("AI > ");
            await foreach (var chunk in chat.SendStreamingAsync(currentSessionId, input))
                Console.Write(chunk);

            Console.WriteLine();
        }
    }

    private static async Task<CommandResult> HandleCommandAsync(string cmd, string arg, IChatStateStore store, string currentSessionId) {
        switch (cmd.ToLowerInvariant()) {
            case "help":
                PrintHelp();
                return new CommandResult(true, null);

            case "save": {
                var state = await store.LoadAsync(currentSessionId, CancellationToken.None);
                await store.SaveAsync(state, CancellationToken.None);
                Console.WriteLine($"Saved session: {state.SessionId}");
                return new CommandResult(true, null);
            }

            case "load": {
                if (string.IsNullOrWhiteSpace(arg)) {
                    Console.WriteLine("Usage: /load <sessionId>");
                    return new CommandResult(true, null);
                }

                var loaded = await store.LoadAsync(arg.Trim(), CancellationToken.None);
                Console.WriteLine($"Loaded session: {loaded.SessionId}");
                return new CommandResult(true, loaded.SessionId);
            }

            case "reset": {
                var state = await store.LoadAsync(currentSessionId, CancellationToken.None);
                state.Turns.Clear();
                state.SummaryMemory = string.Empty;
                state.PreviousResponseId = null;
                await store.SaveAsync(state, CancellationToken.None);
                Console.WriteLine($"Reset session: {currentSessionId}");
                return new CommandResult(true, null);
            }

            case "persona": {
                if (string.IsNullOrWhiteSpace(arg)) {
                    Console.WriteLine("Usage: /persona <stilla>");
                    return new CommandResult(true, null);
                }

                var key = arg.Trim();
                if (!PersonaPresets.TryGetValue(key, out var persona)) {
                    Console.WriteLine("Unknown persona. Available: stilla");
                    return new CommandResult(true, null);
                }

                var state = await store.LoadAsync(currentSessionId, CancellationToken.None);
                state.SystemInstructions = persona;
                await store.SaveAsync(state, CancellationToken.None);
                Console.WriteLine($"Persona set: {key}");
                return new CommandResult(true, null);
            }

            case "export": {
                var state = await store.LoadAsync(currentSessionId, CancellationToken.None);
                Directory.CreateDirectory("exports");
                var path = Path.Combine("exports", $"{currentSessionId}-{DateTime.UtcNow:yyyyMMddHHmmss}.json");
                var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(path, json);
                Console.WriteLine($"Exported: {path}");
                Console.WriteLine(json);
                return new CommandResult(true, null);
            }

            case "import": {
                var importPath = string.IsNullOrWhiteSpace(arg) ? "import.json" : arg.Trim();
                if (!File.Exists(importPath)) {
                    Console.WriteLine($"Import file not found: {importPath}");
                    Console.WriteLine("Usage: /import <path-to-json> (default: import.json)");
                    return new CommandResult(true, null);
                }

                var json = await File.ReadAllTextAsync(importPath);
                var imported = JsonSerializer.Deserialize<ChatSessionState>(json);
                if (imported is null) {
                    Console.WriteLine("Import failed: invalid JSON");
                    return new CommandResult(true, null);
                }

                imported = new ChatSessionState {
                    SessionId = currentSessionId,
                    PreviousResponseId = imported.PreviousResponseId,
                    Turns = imported.Turns ?? new List<ChatTurn>(),
                    SummaryMemory = imported.SummaryMemory ?? string.Empty,
                    SystemInstructions = imported.SystemInstructions
                };

                await store.SaveAsync(imported, CancellationToken.None);
                Console.WriteLine($"Imported to session: {currentSessionId}");
                return new CommandResult(true, null);
            }

            default:
                return new CommandResult(false, null);
        }
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
        Console.WriteLine("Commands: /save /load <id> /reset /persona <preset> /export /import <path> /help /exit");
    }

    private sealed record CommandResult(bool Handled, string? NextSessionId);
}
