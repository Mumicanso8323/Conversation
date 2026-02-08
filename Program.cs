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
        var consoleUi = new ConsoleUi();
        var statusBar = new AffinityStatusBar(consoleUi);

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
        var currentSession = await store.LoadAsync(currentSessionId, CancellationToken.None);
        if (string.IsNullOrWhiteSpace(currentSession.NpcId)) {
            currentSession.NpcId = profiles.DefaultNpcId;
            await store.SaveAsync(currentSession, CancellationToken.None);
        }

        consoleUi.WriteLogLine("Enterで送信。/exit で終了。");
        PrintHelp(consoleUi);

        while (true) {
            currentSession = await store.LoadAsync(currentSessionId, CancellationToken.None);
            profiles = await profileRepository.LoadAsync();
            var npcId = string.IsNullOrWhiteSpace(currentSession.NpcId) ? profiles.DefaultNpcId : currentSession.NpcId;
            var profile = profiles.GetRequiredProfile(npcId);
            var affinity = await affinityEngine.LoadOrCreateAsync(npcId, profile, affinityStore, CancellationToken.None);
            statusBar.Render(npcId, profile.DisplayName, affinity);

            consoleUi.WriteLog($"\n[{currentSessionId}/{npcId}] YOU> ");
            var input = Console.ReadLine();
            if (input is null) break;

            if (TryParseCommand(input, out var cmd, out var arg)) {
                if (string.Equals(cmd, "exit", StringComparison.OrdinalIgnoreCase)) break;

                var result = await HandleCommandAsync(cmd, arg, consoleUi, store, affinityStore, profileRepository, currentSessionId, affinityEngine);
                if (result.NextSessionId is not null) {
                    currentSessionId = result.NextSessionId;
                }

                if (result.ProfileRoot is not null) {
                    profiles = result.ProfileRoot;
                }

                if (!result.Handled) {
                    consoleUi.WriteLogLine($"Unknown command: /{cmd}");
                }

                currentSession = await store.LoadAsync(currentSessionId, CancellationToken.None);
                npcId = string.IsNullOrWhiteSpace(currentSession.NpcId) ? profiles.DefaultNpcId : currentSession.NpcId;
                profile = profiles.GetRequiredProfile(npcId);
                affinity = await affinityEngine.LoadOrCreateAsync(npcId, profile, affinityStore, CancellationToken.None);
                statusBar.Render(npcId, profile.DisplayName, affinity, true);
                continue;
            }

            var delta = await affinityEngine.EvaluateDeltaAsync(input, profile.DisplayName, CancellationToken.None);
            affinityEngine.ApplyDelta(affinity, profile, delta);
            await affinityStore.SaveAsync(affinity, CancellationToken.None);
            statusBar.Render(npcId, profile.DisplayName, affinity, true);

            var roleplayState = affinityEngine.BuildRoleplayStatePrompt(npcId, profile, affinity);
            var forcedReply = affinityEngine.MaybeGenerateBlockedReply(affinity, profile);

            consoleUi.WriteLog("AI > ");
            await foreach (var chunk in chat.SendStreamingAsync(
                currentSessionId,
                input,
                new ChatRequestContext(roleplayState, forcedReply))) {
                consoleUi.WriteLogChunk(chunk);
                statusBar.Render(npcId, profile.DisplayName, affinity);
            }

            consoleUi.WriteLogLine(string.Empty);
            statusBar.Render(npcId, profile.DisplayName, affinity, true);
        }
    }

    private static async Task<CommandResult> HandleCommandAsync(
        string cmd,
        string arg,
        ConsoleUi consoleUi,
        IChatStateStore store,
        IAffinityStore affinityStore,
        JsonAffinityProfileRepository profileRepository,
        string currentSessionId,
        AffinityEngine affinityEngine) {
        var profiles = await profileRepository.LoadAsync();
        switch (cmd.ToLowerInvariant()) {
            case "help":
                PrintHelp(consoleUi);
                return new CommandResult(true, null, profiles);

            case "save": {
                var state = await store.LoadAsync(currentSessionId, CancellationToken.None);
                await store.SaveAsync(state, CancellationToken.None);
                consoleUi.WriteLogLine($"Saved session: {state.SessionId}");
                return new CommandResult(true, null, profiles);
            }

            case "load": {
                if (string.IsNullOrWhiteSpace(arg)) {
                    consoleUi.WriteLogLine("Usage: /load <sessionId>");
                    return new CommandResult(true, null, profiles);
                }

                var loaded = await store.LoadAsync(arg.Trim(), CancellationToken.None);
                if (string.IsNullOrWhiteSpace(loaded.NpcId)) {
                    loaded.NpcId = profiles.DefaultNpcId;
                    await store.SaveAsync(loaded, CancellationToken.None);
                }

                consoleUi.WriteLogLine($"Loaded session: {loaded.SessionId}");
                return new CommandResult(true, loaded.SessionId, profiles);
            }

            case "npc": {
                if (string.IsNullOrWhiteSpace(arg)) {
                    consoleUi.WriteLogLine("Usage: /npc <id>");
                    return new CommandResult(true, null, profiles);
                }

                var state = await store.LoadAsync(currentSessionId, CancellationToken.None);
                state.NpcId = arg.Trim();
                await store.SaveAsync(state, CancellationToken.None);
                var profile = profiles.GetRequiredProfile(state.NpcId);
                await affinityEngine.LoadOrCreateAsync(state.NpcId, profile, affinityStore, CancellationToken.None);
                consoleUi.WriteLogLine($"NPC switched: {state.NpcId}");
                return new CommandResult(true, null, profiles);
            }

            case "aff": {
                var state = await store.LoadAsync(currentSessionId, CancellationToken.None);
                var npcId = string.IsNullOrWhiteSpace(state.NpcId) ? profiles.DefaultNpcId : state.NpcId;
                var profile = profiles.GetRequiredProfile(npcId);
                var affinity = await affinityEngine.LoadOrCreateAsync(npcId, profile, affinityStore, CancellationToken.None);
                consoleUi.WriteLogLine(JsonSerializer.Serialize(affinity, new JsonSerializerOptions { WriteIndented = true }));
                return new CommandResult(true, null, profiles);
            }

            case "set": {
                var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length != 2 || !double.TryParse(parts[1], out var value)) {
                    consoleUi.WriteLogLine("Usage: /set <param> <value>");
                    return new CommandResult(true, null, profiles);
                }

                var session = await store.LoadAsync(currentSessionId, CancellationToken.None);
                var npcId = string.IsNullOrWhiteSpace(session.NpcId) ? profiles.DefaultNpcId : session.NpcId;
                var profile = profiles.GetRequiredProfile(npcId);
                var affinity = await affinityEngine.LoadOrCreateAsync(npcId, profile, affinityStore, CancellationToken.None);
                if (!AffinityEngine.TrySet(affinity, parts[0], value)) {
                    consoleUi.WriteLogLine("Unknown param. like/dislike/liked/disliked/love/hate/trust/respect/sexualAwareness");
                    return new CommandResult(true, null, profiles);
                }

                affinity.UpdatedAt = DateTimeOffset.UtcNow;
                await affinityStore.SaveAsync(affinity, CancellationToken.None);
                consoleUi.WriteLogLine($"Set {parts[0]} = {value:F1}");
                return new CommandResult(true, null, profiles);
            }

            case "profile": {
                var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length > 0 && string.Equals(parts[0], "reload", StringComparison.OrdinalIgnoreCase)) {
                    var reloaded = await profileRepository.LoadAsync();
                    consoleUi.WriteLogLine("Profile reloaded.");
                    return new CommandResult(true, null, reloaded);
                }

                consoleUi.WriteLogLine("Usage: /profile reload");
                return new CommandResult(true, null, profiles);
            }

            case "reset": {
                var state = await store.LoadAsync(currentSessionId, CancellationToken.None);
                state.Turns.Clear();
                state.SummaryMemory = string.Empty;
                state.PreviousResponseId = null;
                await store.SaveAsync(state, CancellationToken.None);
                consoleUi.WriteLogLine($"Reset session: {currentSessionId}");
                return new CommandResult(true, null, profiles);
            }

            case "persona": {
                if (string.IsNullOrWhiteSpace(arg)) {
                    consoleUi.WriteLogLine("Usage: /persona <stilla>");
                    return new CommandResult(true, null, profiles);
                }

                var key = arg.Trim();
                if (!PersonaPresets.TryGetValue(key, out var persona)) {
                    consoleUi.WriteLogLine("Unknown persona. Available: stilla");
                    return new CommandResult(true, null, profiles);
                }

                var state = await store.LoadAsync(currentSessionId, CancellationToken.None);
                state.SystemInstructions = persona;
                await store.SaveAsync(state, CancellationToken.None);
                consoleUi.WriteLogLine($"Persona set: {key}");
                return new CommandResult(true, null, profiles);
            }

            case "export": {
                var state = await store.LoadAsync(currentSessionId, CancellationToken.None);
                Directory.CreateDirectory("exports");
                var path = Path.Combine("exports", $"{currentSessionId}-{DateTime.UtcNow:yyyyMMddHHmmss}.json");
                var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(path, json);
                consoleUi.WriteLogLine($"Exported: {path}");
                consoleUi.WriteLogLine(json);
                return new CommandResult(true, null, profiles);
            }

            case "import": {
                var importPath = string.IsNullOrWhiteSpace(arg) ? "import.json" : arg.Trim();
                if (!File.Exists(importPath)) {
                    consoleUi.WriteLogLine($"Import file not found: {importPath}");
                    consoleUi.WriteLogLine("Usage: /import <path-to-json> (default: import.json)");
                    return new CommandResult(true, null, profiles);
                }

                var json = await File.ReadAllTextAsync(importPath);
                var imported = JsonSerializer.Deserialize<ChatSessionState>(json);
                if (imported is null) {
                    consoleUi.WriteLogLine("Import failed: invalid JSON");
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
                consoleUi.WriteLogLine($"Imported to session: {currentSessionId}");
                return new CommandResult(true, null, profiles);
            }

            default:
                return new CommandResult(false, null, profiles);
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

    private static void PrintHelp(ConsoleUi consoleUi) {
        consoleUi.WriteLogLine("Commands: /save /load <id> /npc <id> /aff /set <k> <v> /profile reload /reset /persona <preset> /export /import <path> /help /exit");
    }

    private sealed record CommandResult(bool Handled, string? NextSessionId, AffinityProfileRoot? ProfileRoot);

    private sealed class AffinityStatusBar {
        private readonly ConsoleUi _consoleUi;
        private DateTime _lastRender = DateTime.MinValue;

        public AffinityStatusBar(ConsoleUi consoleUi) {
            _consoleUi = consoleUi;
        }

        public void Render(string npcId, string displayName, AffinityState state, bool force = false) {
            if (!force && (DateTime.UtcNow - _lastRender).TotalMilliseconds < 120) {
                return;
            }

            _lastRender = DateTime.UtcNow;
            var text = $"NPC:{npcId}({displayName}) Like {state.Like:F1} Dislike {state.Dislike:F1} | Liked {state.Liked:F1} Disliked {state.Disliked:F1} | Love {state.Love:F1} Hate {state.Hate:F1} | Trust {state.Trust:F1} Respect {state.Respect:F1}";
            _consoleUi.SetStatus(text, force);
        }
    }

    private sealed partial class ConsoleUi {
        private const string Esc = "\u001b";
        private readonly object _gate = new();
        private readonly bool _useAnsiStatus;
        private string _statusText = string.Empty;
        private int _lastWidth = -1;
        private int _lastHeight = -1;

        public ConsoleUi() {
            _useAnsiStatus = !Console.IsOutputRedirected && TryEnableAnsiStatus();
        }

        public void SetStatus(string text, bool force = false) {
            lock (_gate) {
            if (Console.IsOutputRedirected) {
                if (force) {
                    Console.WriteLine(text);
                }

                _statusText = text;
                return;
            }

            EnsureLayout();
            if (!force && string.Equals(_statusText, text, StringComparison.Ordinal)) {
                return;
            }

            _statusText = text;
            RenderStatusLine();
            }
        }

        public void WriteLog(string text) {
            WriteInternal(text, appendNewLine: false);
        }

        public void WriteLogLine(string text) {
            WriteInternal(text, appendNewLine: true);
        }

        public void WriteLogChunk(string chunk) {
            WriteInternal(chunk, appendNewLine: false);
        }

        private void WriteInternal(string text, bool appendNewLine) {
            lock (_gate) {
            if (Console.IsOutputRedirected) {
                if (appendNewLine) {
                    Console.WriteLine(text);
                }
                else {
                    Console.Write(text);
                }

                return;
            }

            EnsureLayout();
            EnsureCursorInLogArea();

            WriteLogText(text);

            if (appendNewLine) {
                WriteNewLine();
            }

            EnsureCursorInLogArea();
            RenderStatusLine();
            }
        }

        private void WriteLogText(string text) {
            if (string.IsNullOrEmpty(text)) {
                return;
            }

            int i = 0;
            while (i < text.Length) {
                int newLineIndex = text.IndexOf('\n', i);
                bool hasNewLine = newLineIndex >= 0;
                string segment = hasNewLine ? text[i..newLineIndex] : text[i..];

                if (segment.Length > 0) {
                    if (segment[^1] == '\r') {
                        segment = segment[..^1];
                    }

                    WriteSegment(segment);
                }

                if (!hasNewLine) {
                    break;
                }

                WriteNewLine();
                i = newLineIndex + 1;
            }
        }

        private void WriteSegment(string segment) {
            int offset = 0;
            while (offset < segment.Length) {
                EnsureCursorInLogArea();
                int width = Math.Max(1, Console.WindowWidth);
                int available = Math.Max(1, width - Console.CursorLeft);
                int remaining = segment.Length - offset;
                int take = Math.Min(available, remaining);
                Console.Write(segment.Substring(offset, take));
                offset += take;

                if (offset < segment.Length) {
                    if (Console.CursorTop >= LogBottom) {
                        ScrollLogArea();
                    }
                    else {
                        SafeSetCursor(0, Console.CursorTop + 1);
                    }
                }
            }
        }

        private void WriteNewLine() {
            if (Console.CursorTop >= LogBottom) {
                ScrollLogArea();
                return;
            }

            Console.WriteLine();
            EnsureCursorInLogArea();
        }

        private void ScrollLogArea() {
            SafeSetCursor(0, LogBottom);
            Console.WriteLine();
            SafeSetCursor(0, LogBottom);
        }

        private void EnsureLayout() {
            if (Console.IsOutputRedirected) {
                return;
            }

            int width = Math.Max(1, Console.WindowWidth);
            int height = Math.Max(1, Console.WindowHeight);
            if (width == _lastWidth && height == _lastHeight) {
                return;
            }

            _lastWidth = width;
            _lastHeight = height;
            EnsureCursorInLogArea();
            RenderStatusLine();
        }

        private void EnsureCursorInLogArea() {
            int left = Math.Clamp(Console.CursorLeft, 0, Math.Max(0, Console.WindowWidth - 1));
            int top = Math.Clamp(Console.CursorTop, 0, LogBottom);
            SafeSetCursor(left, top);
        }

        private void RenderStatusLine() {
            if (Console.IsOutputRedirected) {
                return;
            }

            string fitted = FitStatusText();
            if (_useAnsiStatus) {
                int bottomRow = Math.Max(1, Console.WindowHeight);
                Console.Write($"{Esc}[s{Esc}[{bottomRow};1H{Esc}[2K");
                Console.Write(fitted);
                Console.Write($"{Esc}[u");
                return;
            }

            int curLeft = Console.CursorLeft;
            int curTop = Console.CursorTop;
            int restoreTop = Math.Clamp(curTop, 0, LogBottom);
            int restoreLeft = Math.Clamp(curLeft, 0, Math.Max(0, Math.Max(1, Console.WindowWidth) - 1));

            SafeSetCursor(0, StatusLine);
            int effectiveWidth = Math.Max(0, Math.Max(1, Console.WindowWidth) - 1);
            if (effectiveWidth > 0) {
                Console.Write(new string(' ', effectiveWidth));
                SafeSetCursor(0, StatusLine);
                Console.Write(fitted);
            }

            SafeSetCursor(restoreLeft, restoreTop);
        }

        private string FitStatusText() {
            int effectiveWidth = Math.Max(0, Math.Max(1, Console.WindowWidth) - 1);
            string normalized = (_statusText ?? string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);
            if (effectiveWidth == 0) {
                return string.Empty;
            }

            return normalized.Length > effectiveWidth
                ? normalized[..effectiveWidth]
                : normalized.PadRight(effectiveWidth);
        }

        private int StatusLine => Math.Max(0, Console.WindowHeight - 1);

        private int LogBottom => Math.Max(0, Console.WindowHeight - 2);

        private static void SafeSetCursor(int left, int top) {
            try {
                Console.SetCursorPosition(left, top);
            }
            catch (ArgumentOutOfRangeException) {
            }
            catch (IOException) {
            }
        }

        private static bool TryEnableAnsiStatus() {
            if (!OperatingSystem.IsWindows()) {
                return true;
            }

            const int stdOutputHandle = -11;
            const uint enableVirtualTerminalProcessing = 0x0004;

            var handle = GetStdHandle(stdOutputHandle);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) {
                return false;
            }

            uint mode = 0;
            if (!GetConsoleMode(handle, out mode)) {
                return false;
            }

            if ((mode & enableVirtualTerminalProcessing) != 0) {
                return true;
            }

            return SetConsoleMode(handle, mode | enableVirtualTerminalProcessing);
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
    }
}
