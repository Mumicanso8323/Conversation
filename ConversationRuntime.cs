namespace Conversation;

using Conversation.Affinity;
using Conversation.Config;
using Conversation.Diagnostics;
using Conversation.Psyche;
using Conversation.Standee;
using Conversation.ViewModels;
using System.IO;
using System.Text;
using System.Text.Json;

public sealed class ConversationRuntime {
    private readonly IChatStateStore _store;
    private readonly IAffinityStore _affinityStore;
    private readonly IPsycheStore _psycheStore;
    private readonly JsonAffinityProfileRepository _profileRepository;
    private readonly JsonPsycheProfileRepository _psycheProfileRepository;
    private readonly PsycheOrchestrator _psycheOrchestrator;
    private readonly StateNarrator _stateNarrator;
    private readonly JsonPromptRepository _promptRepository;
    private PromptRoot _prompts = PromptRoot.CreateDefault();
    private readonly IStandeeController? _standeeController;

    private UniversalChatModule? _chat;
    private PsycheJudge? _psycheJudge;
    private StandeeJudge? _standeeJudge;
    private SceneJudge? _sceneJudge;
    private string? _activeApiKey;
    private string? _activeMainModel;
    private string? _activeStandeeModel;
    private string? _activeSummaryModel;
    private ChatEngineMode? _activeMainMode;
    private string _configurationInfoMessage = string.Empty;

    public string CurrentSessionId { get; private set; } = "stilla";
    public bool LastCommandRequestedExit { get; private set; }
    private bool _standeeEnabled = true;
    private string _currentNpcDisplayName = "キャラクター";

    public bool IsConfigured { get; private set; }
    public string ConfigurationErrorMessage { get; private set; } = string.Empty;
    public string ConfigurationInfoMessage => _configurationInfoMessage;
    public AppSettings Settings { get; }

    public ConversationRuntime(IStandeeController? standeeController = null) {
        _standeeController = standeeController;
        Settings = AppSettings.Load();
        _standeeEnabled = Settings.Standee.Enabled;

        TryEnsureProfileCopy(AppPaths.DataNpcProfilesPath, AppPaths.BaseNpcProfilesPath);
        TryEnsureProfileCopy(AppPaths.DataPsycheProfilesPath, AppPaths.BasePsycheProfilesPath);
        TryEnsureProfileCopy(AppPaths.DataPromptsPath, AppPaths.BasePromptsPath);

        _store = new CompatibleChatStateStore(AppPaths.SessionsDir, AppPaths.BaseSessionsDir);
        _affinityStore = new CompatibleAffinityStore(AppPaths.NpcStatesDir, AppPaths.BaseNpcStatesDir);
        _psycheStore = new CompatiblePsycheStore(AppPaths.PsycheStatesDir, AppPaths.BasePsycheStatesDir);

        _profileRepository = new JsonAffinityProfileRepository(File.Exists(AppPaths.DataNpcProfilesPath) ? AppPaths.DataNpcProfilesPath : AppPaths.BaseNpcProfilesPath);
        _psycheProfileRepository = new JsonPsycheProfileRepository(File.Exists(AppPaths.DataPsycheProfilesPath) ? AppPaths.DataPsycheProfilesPath : AppPaths.BasePsycheProfilesPath);

        _psycheOrchestrator = new PsycheOrchestrator();
        _stateNarrator = new StateNarrator();
        _promptRepository = new JsonPromptRepository();

    }

    public void SaveSettings() {
        Settings.Save();
    }

    public async Task<bool> TryInitializeAsync() {
        try {
            var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey)) {
                IsConfigured = false;
                ConfigurationErrorMessage = "OPENAI_API_KEY が設定されていません。環境変数を設定して再起動してください。";
                return false;
            }

            var mainModel = string.IsNullOrWhiteSpace(Settings.Models.MainChat) ? "gpt-5.2" : Settings.Models.MainChat;
            var standeeModel = string.IsNullOrWhiteSpace(Settings.Models.StandeeJudge) ? "gpt-5.1" : Settings.Models.StandeeJudge;
            var summaryModel = string.IsNullOrWhiteSpace(Settings.Models.SummaryModel) ? "gpt-5.2" : Settings.Models.SummaryModel;
            var selectedMode = ParseChatEngineMode(Settings.Models.MainChatEngineMode);
            var requiredMode = GetRequiredEngineMode(mainModel);
            var effectiveMode = requiredMode ?? selectedMode;
            _configurationInfoMessage = requiredMode is not null && selectedMode != requiredMode.Value
                ? "このモデルは Chat API 非対応のため Responses API を使用します。"
                : string.Empty;
            _prompts = await _promptRepository.LoadAsync();

            if (_chat is null || _psycheJudge is null || _standeeJudge is null || _sceneJudge is null || _activeApiKey != apiKey || _activeMainModel != mainModel || _activeStandeeModel != standeeModel || _activeSummaryModel != summaryModel || _activeMainMode != effectiveMode) {
                _chat = new UniversalChatModule(
                    new ChatModuleOptions(
                        Model: mainModel,
                        ApiKey: apiKey,
                        SystemInstructions: _prompts.GetPersonaSystemInstructions("stilla"),
                        Mode: effectiveMode,
                        Streaming: true,
                        SummaryModel: summaryModel,
                        PersonaSystemResolver: personaId => _prompts.GetPersonaSystemInstructions(personaId)
                    ),
                    _store
                );

                _chat.AddChatFunctionTool(
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

                _psycheJudge = new PsycheJudge("gpt-5.1", apiKey, () => _prompts.GetModulePrompt("psyche_judge", string.Empty));
                _standeeJudge = new StandeeJudge(standeeModel, apiKey, () => _prompts.GetModulePrompt("standee_judge", string.Empty));
                _sceneJudge = new SceneJudge(standeeModel, apiKey, () => _prompts.GetModulePrompt("scene_judge", string.Empty));
                _activeApiKey = apiKey;
                _activeMainModel = mainModel;
                _activeStandeeModel = standeeModel;
                _activeSummaryModel = summaryModel;
                _activeMainMode = effectiveMode;
            }

            IsConfigured = true;
            ConfigurationErrorMessage = string.Empty;
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex) {
            Log.Error(ex, "TryInitializeAsync");
            IsConfigured = false;
            ConfigurationErrorMessage = $"初期化に失敗しました: {ex.Message}";
            _configurationInfoMessage = string.Empty;
            return false;
        }
    }

    private static ChatEngineMode? GetRequiredEngineMode(string modelName) {
        return modelName.Contains("-pro", StringComparison.OrdinalIgnoreCase)
            ? ChatEngineMode.ResponsesManualHistory
            : null;
    }

    private static ChatEngineMode ParseChatEngineMode(string? configuredMode) {
        if (string.Equals(configuredMode, "ResponsesManualHistory", StringComparison.OrdinalIgnoreCase)) {
            return ChatEngineMode.ResponsesManualHistory;
        }

        if (string.Equals(configuredMode, "ResponsesChained", StringComparison.OrdinalIgnoreCase)) {
            return ChatEngineMode.ResponsesChained;
        }

        return ChatEngineMode.ChatCompletions;
    }

    public async Task EnsureInitializedAsync(CancellationToken ct) {
        var profiles = await _profileRepository.LoadAsync(ct);
        _prompts = await _promptRepository.LoadAsync(ct);
        var currentSession = await _store.LoadAsync(CurrentSessionId, ct);
        if (string.IsNullOrWhiteSpace(currentSession.NpcId)) {
            currentSession.NpcId = profiles.DefaultNpcId;
            await _store.SaveAsync(currentSession, ct);
        }

        var currentNpcId = string.IsNullOrWhiteSpace(currentSession.NpcId) ? profiles.DefaultNpcId : currentSession.NpcId;
        _currentNpcDisplayName = profiles.GetRequiredProfile(currentNpcId).DisplayName;
    }

    public string GetCurrentNpcDisplayName() => _currentNpcDisplayName;

    public async Task<IReadOnlyList<ChatMessage>> GetLastTurnsAsync(string sessionId, int count, string userDisplayName, CancellationToken ct) {
        var state = await _store.LoadAsync(sessionId, ct);
        if (state.Turns.Count == 0) return Array.Empty<ChatMessage>();

        var profiles = await _profileRepository.LoadAsync(ct);
        var npcId = string.IsNullOrWhiteSpace(state.NpcId) ? profiles.DefaultNpcId : state.NpcId;
        var npcDisplayName = profiles.GetRequiredProfile(npcId).DisplayName;
        _currentNpcDisplayName = npcDisplayName;

        int n = Math.Min(count, state.Turns.Count);
        var lines = new List<ChatMessage>(n + 1);
        for (int i = state.Turns.Count - n; i < state.Turns.Count; i++) {
            var turn = state.Turns[i];
            var role = (turn.Role ?? string.Empty).ToLowerInvariant();
            switch (role) {
                case "assistant":
                    lines.Add(new ChatMessage {
                        Role = "assistant",
                        Speaker = npcDisplayName,
                        Text = EnsureTrailingNewline(turn.Text),
                    });
                    break;
                case "user":
                    lines.Add(new ChatMessage {
                        Role = "user",
                        Speaker = userDisplayName,
                        Text = turn.Text,
                    });
                    break;
                default:
                    lines.Add(new ChatMessage {
                        Role = "system",
                        Speaker = "システム",
                        Text = turn.Text,
                    });
                    break;
            }
        }

        lines.Add(new ChatMessage { Role = "separator", Text = "—" });
        return lines;
    }

    public async Task RunTurnAsync(string sessionId, string input, ITranscriptSink sink, CancellationToken ct) {
        LastCommandRequestedExit = false;
        CurrentSessionId = sessionId;

        if (!await TryInitializeAsync()) {
            sink.AppendSystemLine(ConfigurationErrorMessage);
            return;
        }

        _prompts = await _promptRepository.LoadAsync(ct);
        var currentSession = await _store.LoadAsync(CurrentSessionId, ct);
        var profiles = await _profileRepository.LoadAsync(ct);
        var psycheProfiles = await _psycheProfileRepository.LoadAsync(ct);
        var npcId = string.IsNullOrWhiteSpace(currentSession.NpcId) ? profiles.DefaultNpcId : currentSession.NpcId;
        var profile = profiles.GetRequiredProfile(npcId);
        _currentNpcDisplayName = profile.DisplayName;
        var psycheProfile = psycheProfiles.GetRequiredProfile(npcId);

        var affinityEngine = new AffinityEngine("gpt-5.1", _activeApiKey!);
        var affinity = await affinityEngine.LoadOrCreateAsync(npcId, profile, _affinityStore, ct);
        var psyche = await _psycheOrchestrator.LoadOrCreateAsync(npcId, psycheProfile, _psycheStore, ct);

        if (TryParseCommand(input, out var cmd, out var arg)) {
            if (string.Equals(cmd, "exit", StringComparison.OrdinalIgnoreCase)) {
                LastCommandRequestedExit = true;
                sink.AppendSystemLine("終了が要求されました。");
                return;
            }

            var result = await HandleCommandAsync(cmd, arg, CurrentSessionId, sink, affinityEngine, ct);
            if (result.NextSessionId is not null) {
                CurrentSessionId = result.NextSessionId;
            }

            if (!result.Handled) {
                sink.AppendSystemLine($"不明なコマンドです: /{cmd}");
            }

            return;
        }

        var assistantStarted = false;
        try {
            var delta = await affinityEngine.EvaluateDeltaAsync(input, profile.DisplayName, ct);
            affinityEngine.ApplyDelta(affinity, profile, delta);
            await _affinityStore.SaveAsync(affinity, ct);

            var recentContext = BuildRecentContext(currentSession.Turns, 6);
            var narrated = _stateNarrator.BuildJudgeStateText(npcId, psycheProfile, affinity, psyche, input, recentContext);
            var judged = await _psycheJudge!.EvaluateAsync(input, profile.DisplayName, recentContext, narrated, ct);

            var sprite = await _standeeJudge!.EvaluateAsync(input, profile.DisplayName, recentContext, narrated, ct);
            if (_standeeEnabled && _standeeController is not null) {
                await _standeeController.SetSpriteAsync(sprite, ct);
            }

            _psycheOrchestrator.ApplyDelta(psyche, psycheProfile, judged.PsycheDelta);
            await _psycheStore.SaveAsync(psyche, ct);

            var roleplayState = affinityEngine.BuildRoleplayStatePrompt(npcId, profile, affinity);
            var forcedReply = affinityEngine.MaybeGenerateBlockedReply(affinity, profile);
            var additionalSystem = $"{roleplayState}\n\n{judged.Directives.ToSystemBlock()}";

            sink.BeginAssistantLine();
            assistantStarted = true;
            await foreach (var chunk in _chat!.SendStreamingAsync(CurrentSessionId, input, new ChatRequestContext(additionalSystem, forcedReply), ct)) {
                sink.AppendAssistantDelta(chunk);
            }
        }
        catch (OperationCanceledException) {
            throw;
        }
        catch (Exception ex) {
            sink.AppendSystemLine($"モデル呼び出しに失敗しました: {ex.Message}");
            Log.Error(ex, "RunTurnAsync model pipeline");
        }
        finally {
            if (assistantStarted) {
                sink.FinalizeAssistantLine();
            }
        }
    }

    private async Task<CommandResult> HandleCommandAsync(string cmd, string arg, string currentSessionId, ITranscriptSink sink, AffinityEngine affinityEngine, CancellationToken ct) {
        var profiles = await _profileRepository.LoadAsync(ct);
        var psycheProfiles = await _psycheProfileRepository.LoadAsync(ct);
        switch (cmd.ToLowerInvariant()) {
            case "help":
                sink.AppendSystemLine("コマンド一覧: /session [id] /save /load <id> /npc <id> /aff /psy [json|reset|set <k> <v>] /set <k> <v> /profile reload /reset /persona <personaId> /export /import <path> /standee on|off|show|hide|sprite <file> /help /exit");
                return new CommandResult(true, null);
            case "session": {
                if (string.IsNullOrWhiteSpace(arg)) {
                    sink.AppendSystemLine($"現在のセッション: {CurrentSessionId}");
                    return new CommandResult(true, null);
                }
                var next = arg.Trim();
                await SwitchSessionAsync(next, ct);
                sink.AppendSystemLine($"セッションを切り替えました: {CurrentSessionId}");
                return new CommandResult(true, CurrentSessionId);
            }
            case "standee":
                return await HandleStandeeCommandAsync(arg, sink, ct);
            case "save": {
                var state = await _store.LoadAsync(currentSessionId, ct);
                await _store.SaveAsync(state, ct);
                sink.AppendSystemLine($"セッションを保存しました: {state.SessionId}");
                return new CommandResult(true, null);
            }
            case "load": {
                if (string.IsNullOrWhiteSpace(arg)) {
                    sink.AppendSystemLine("使い方: /load <sessionId>");
                    return new CommandResult(true, null);
                }

                var loaded = await _store.LoadAsync(arg.Trim(), ct);
                if (string.IsNullOrWhiteSpace(loaded.NpcId)) {
                    loaded.NpcId = profiles.DefaultNpcId;
                    await _store.SaveAsync(loaded, ct);
                }

                sink.AppendSystemLine($"セッションを読み込みました: {loaded.SessionId}");
                return new CommandResult(true, loaded.SessionId);
            }
            case "npc": {
                if (string.IsNullOrWhiteSpace(arg)) {
                    sink.AppendSystemLine("使い方: /npc <id>");
                    return new CommandResult(true, null);
                }

                var state = await _store.LoadAsync(currentSessionId, ct);
                state.NpcId = arg.Trim();
                await _store.SaveAsync(state, ct);
                var profile = profiles.GetRequiredProfile(state.NpcId);
                await affinityEngine.LoadOrCreateAsync(state.NpcId, profile, _affinityStore, ct);
                sink.AppendSystemLine($"キャラクターを切り替えました: {state.NpcId}");
                return new CommandResult(true, null);
            }
            case "aff": {
                var state = await _store.LoadAsync(currentSessionId, ct);
                var npcId = string.IsNullOrWhiteSpace(state.NpcId) ? profiles.DefaultNpcId : state.NpcId;
                var profile = profiles.GetRequiredProfile(npcId);
                var affinity = await affinityEngine.LoadOrCreateAsync(npcId, profile, _affinityStore, ct);
                sink.AppendSystemLine(JsonSerializer.Serialize(affinity, new JsonSerializerOptions { WriteIndented = true }));
                return new CommandResult(true, null);
            }
            case "set": {
                var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length != 2 || !double.TryParse(parts[1], out var value)) {
                    sink.AppendSystemLine("使い方: /set <param> <value>");
                    return new CommandResult(true, null);
                }

                var session = await _store.LoadAsync(currentSessionId, ct);
                var npcId = string.IsNullOrWhiteSpace(session.NpcId) ? profiles.DefaultNpcId : session.NpcId;
                var profile = profiles.GetRequiredProfile(npcId);
                var affinity = await affinityEngine.LoadOrCreateAsync(npcId, profile, _affinityStore, ct);
                if (AffinityEngine.TrySet(affinity, parts[0], value)) {
                    affinity.UpdatedAt = DateTimeOffset.UtcNow;
                    await _affinityStore.SaveAsync(affinity, ct);
                    sink.AppendSystemLine($"{parts[0]} を {value:F1} に設定しました");
                    return new CommandResult(true, null);
                }

                var psycheProfile = psycheProfiles.GetRequiredProfile(npcId);
                var psyche = await _psycheOrchestrator.LoadOrCreateAsync(npcId, psycheProfile, _psycheStore, ct);
                if (!PsycheSetter.TrySet(psyche, parts[0], value, out _)) {
                    sink.AppendSystemLine("不明なパラメータです。affinity: like/dislike/liked/disliked/love/hate/trust/respect/sexualAwareness psyche: desire.<axis>.trait|deficit|gain libido.<axis>.trait|deficit|gain mood.current.valence|arousal|control mood.baseline.valence|arousal|control");
                    return new CommandResult(true, null);
                }

                await _psycheStore.SaveAsync(psyche, ct);
                sink.AppendSystemLine($"{parts[0]} を {value:F1} に設定しました");
                return new CommandResult(true, null);
            }
            case "psy": {
                var state = await _store.LoadAsync(currentSessionId, ct);
                var npcId = string.IsNullOrWhiteSpace(state.NpcId) ? profiles.DefaultNpcId : state.NpcId;
                var profile = profiles.GetRequiredProfile(npcId);
                var psycheProfile = psycheProfiles.GetRequiredProfile(npcId);
                var psyche = await _psycheOrchestrator.LoadOrCreateAsync(npcId, psycheProfile, _psycheStore, ct);
                var affinity = await affinityEngine.LoadOrCreateAsync(npcId, profile, _affinityStore, ct);

                var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 0) {
                    sink.AppendSystemLine(BuildPsycheSummary(npcId, psycheProfile, affinity, psyche, BuildRecentContext(state.Turns, 6)));
                    return new CommandResult(true, null);
                }
                if (parts.Length == 1 && string.Equals(parts[0], "json", StringComparison.OrdinalIgnoreCase)) {
                    sink.AppendSystemLine(JsonSerializer.Serialize(psyche, new JsonSerializerOptions { WriteIndented = true }));
                    return new CommandResult(true, null);
                }
                if (parts.Length == 1 && string.Equals(parts[0], "reset", StringComparison.OrdinalIgnoreCase)) {
                    await _psycheStore.DeleteAsync(npcId, ct);
                    psyche = await _psycheOrchestrator.LoadOrCreateAsync(npcId, psycheProfile, _psycheStore, ct);
                    sink.AppendSystemLine($"心理状態をリセットしました: {npcId}");
                    sink.AppendSystemLine(BuildPsycheSummary(npcId, psycheProfile, affinity, psyche, BuildRecentContext(state.Turns, 6)));
                    return new CommandResult(true, null);
                }
                if (parts.Length == 3 && string.Equals(parts[0], "set", StringComparison.OrdinalIgnoreCase) && double.TryParse(parts[2], out var value)) {
                    if (!PsycheSetter.TrySet(psyche, parts[1], value, out var error)) {
                        sink.AppendSystemLine($"/psy set に失敗しました: {error}");
                        sink.AppendSystemLine("使い方: /psy set <param> <value>");
                        return new CommandResult(true, null);
                    }

                    await _psycheStore.SaveAsync(psyche, ct);
                    sink.AppendSystemLine($"{parts[1]} を {value:F1} に設定しました");
                    return new CommandResult(true, null);
                }

                sink.AppendSystemLine("使い方: /psy [/json|reset|set <k> <v>]");
                return new CommandResult(true, null);
            }
            case "profile": {
                var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length > 0 && string.Equals(parts[0], "reload", StringComparison.OrdinalIgnoreCase)) {
                    await _profileRepository.LoadAsync(ct);
                    sink.AppendSystemLine("プロフィールを再読み込みしました。");
                    return new CommandResult(true, null);
                }

                sink.AppendSystemLine("使い方: /profile reload");
                return new CommandResult(true, null);
            }
            case "reset": {
                var state = await _store.LoadAsync(currentSessionId, ct);
                state.Turns.Clear();
                state.SummaryMemory = string.Empty;
                state.PreviousResponseId = null;
                await _store.SaveAsync(state, ct);
                sink.AppendSystemLine($"セッションをリセットしました: {currentSessionId}");
                return new CommandResult(true, null);
            }
            case "persona": {
                if (string.IsNullOrWhiteSpace(arg)) {
                    sink.AppendSystemLine("使い方: /persona <personaId>");
                    return new CommandResult(true, null);
                }

                var key = arg.Trim();
                if (!_prompts.Personas.ContainsKey(key)) {
                    var available = string.Join(", ", _prompts.Personas.Keys.OrderBy(x => x));
                    sink.AppendSystemLine($"不明なペルソナです。利用可能: {available}");
                    return new CommandResult(true, null);
                }

                var state = await _store.LoadAsync(currentSessionId, ct);
                state.PersonaId = key;
                await _store.SaveAsync(state, ct);
                sink.AppendSystemLine($"ペルソナを設定しました: {key}");
                return new CommandResult(true, null);
            }
            case "export": {
                var state = await _store.LoadAsync(currentSessionId, ct);
                var exportsDir = Path.Combine(AppPaths.DataRoot, "exports");
                Directory.CreateDirectory(exportsDir);
                var path = Path.Combine(exportsDir, $"{currentSessionId}-{DateTime.UtcNow:yyyyMMddHHmmss}.json");
                var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(path, json, ct);
                sink.AppendSystemLine($"エクスポートしました: {path}");
                sink.AppendSystemLine(json);
                return new CommandResult(true, null);
            }
            case "import": {
                var importPath = string.IsNullOrWhiteSpace(arg) ? Path.Combine(AppPaths.DataRoot, "import.json") : arg.Trim();
                if (!Path.IsPathRooted(importPath)) {
                    importPath = Path.Combine(AppPaths.DataRoot, importPath);
                }

                if (!File.Exists(importPath)) {
                    sink.AppendSystemLine($"インポートファイルが見つかりません: {importPath}");
                    sink.AppendSystemLine("使い方: /import <path-to-json>（既定: import.json）");
                    return new CommandResult(true, null);
                }

                var json = await File.ReadAllTextAsync(importPath, ct);
                var imported = JsonSerializer.Deserialize<ChatSessionState>(json);
                if (imported is null) {
                    sink.AppendSystemLine("インポート失敗: JSONが不正です");
                    return new CommandResult(true, null);
                }

                imported = new ChatSessionState {
                    SessionId = currentSessionId,
                    PreviousResponseId = imported.PreviousResponseId,
                    Turns = imported.Turns ?? new List<ChatTurn>(),
                    SummaryMemory = imported.SummaryMemory ?? string.Empty,
                    SystemInstructions = imported.SystemInstructions,
                    PersonaId = imported.PersonaId,
                    NpcId = string.IsNullOrWhiteSpace(imported.NpcId) ? profiles.DefaultNpcId : imported.NpcId
                };

                await _store.SaveAsync(imported, ct);
                sink.AppendSystemLine($"セッションへインポートしました: {currentSessionId}");
                return new CommandResult(true, null);
            }
            case "clear":
                sink.AppendSystemLine("__CLEAR_TRANSCRIPT__");
                return new CommandResult(true, null);
            default:
                return new CommandResult(false, null);
        }
    }

    private async Task<CommandResult> HandleStandeeCommandAsync(string arg, ITranscriptSink sink, CancellationToken ct) {
        var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) {
            sink.AppendSystemLine("使い方: /standee on|off|show|hide|sprite <exact filename>");
            return new CommandResult(true, null);
        }

        switch (parts[0].ToLowerInvariant()) {
            case "on":
                _standeeEnabled = true;
                Settings.Standee.Enabled = true;
                if (_standeeController is not null) await _standeeController.ShowAsync(ct);
                sink.AppendSystemLine("立ち絵表示を有効にしました。");
                return new CommandResult(true, null);
            case "off":
                _standeeEnabled = false;
                Settings.Standee.Enabled = false;
                if (_standeeController is not null) await _standeeController.HideAsync(ct);
                sink.AppendSystemLine("立ち絵表示を無効にしました。");
                return new CommandResult(true, null);
            case "show":
                if (_standeeController is not null) await _standeeController.ShowAsync(ct);
                sink.AppendSystemLine("立ち絵を表示しました。");
                return new CommandResult(true, null);
            case "hide":
                if (_standeeController is not null) await _standeeController.HideAsync(ct);
                sink.AppendSystemLine("立ち絵を非表示にしました。");
                return new CommandResult(true, null);
            case "sprite": {
                if (parts.Length < 2) {
                    sink.AppendSystemLine("使い方: /standee sprite <exact filename>");
                    return new CommandResult(true, null);
                }

                var sprite = StandeeSprites.IsAllowed(parts[1]) ? parts[1] : StandeeSprites.Default;
                if (_standeeController is not null) await _standeeController.SetSpriteAsync(sprite, ct);
                sink.AppendSystemLine($"立ち絵スプライト: {sprite}");
                return new CommandResult(true, null);
            }
            default:
                sink.AppendSystemLine("使い方: /standee on|off|show|hide|sprite <exact filename>");
                return new CommandResult(true, null);
        }
    }



    public async Task<PromptRoot> LoadPromptsAsync(CancellationToken ct = default) {
        _prompts = await _promptRepository.LoadAsync(ct);
        return _prompts;
    }

    public async Task SavePromptsAsync(PromptRoot root, CancellationToken ct = default) {
        await _promptRepository.SaveAsync(root, ct);
        _prompts = root;
    }

    public async Task RestoreDefaultPromptsAsync(CancellationToken ct = default) {
        await _promptRepository.SaveDefaultAsync(ct);
        _prompts = await _promptRepository.LoadAsync(ct);
    }

    public async Task ReloadPsycheProfilesAsync(CancellationToken ct = default) {
        await _psycheProfileRepository.LoadAsync(ct);
    }

    public async Task<AffinityState> LoadAffinityAsync(string npcId, CancellationToken ct = default) {
        var profiles = await _profileRepository.LoadAsync(ct);
        var id = string.IsNullOrWhiteSpace(npcId) ? profiles.DefaultNpcId : npcId;
        var profile = profiles.GetRequiredProfile(id);
        var affinityEngine = new AffinityEngine("gpt-5.1", _activeApiKey ?? string.Empty);
        return await affinityEngine.LoadOrCreateAsync(id, profile, _affinityStore, ct);
    }

    public async Task SaveAffinityAsync(AffinityState state, CancellationToken ct = default) {
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await _affinityStore.SaveAsync(state, ct);
    }

    public async Task<AffinityState> ResetAffinityAsync(string npcId, CancellationToken ct = default) {
        var profiles = await _profileRepository.LoadAsync(ct);
        var id = string.IsNullOrWhiteSpace(npcId) ? profiles.DefaultNpcId : npcId;
        var profile = profiles.GetRequiredProfile(id);
        var reset = new AffinityState {
            NpcId = id,
            Like = profile.Initial.Like,
            Dislike = profile.Initial.Dislike,
            Liked = profile.Initial.Liked,
            Disliked = profile.Initial.Disliked,
            Love = profile.Initial.Love,
            Hate = profile.Initial.Hate,
            Trust = profile.Initial.Trust,
            Respect = profile.Initial.Respect,
            SexualAwareness = profile.Initial.SexualAwareness,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await _affinityStore.SaveAsync(reset, ct);
        return reset;
    }

    public async Task<PsycheState> LoadPsycheAsync(string npcId, CancellationToken ct = default) {
        var profiles = await _psycheProfileRepository.LoadAsync(ct);
        var id = string.IsNullOrWhiteSpace(npcId) ? profiles.DefaultNpcId : npcId;
        var profile = profiles.GetRequiredProfile(id);
        return await _psycheOrchestrator.LoadOrCreateAsync(id, profile, _psycheStore, ct);
    }

    public async Task SavePsycheAsync(PsycheState state, CancellationToken ct = default) {
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await _psycheStore.SaveAsync(state, ct);
    }

    public async Task<PsycheState> ResetPsycheAsync(string npcId, CancellationToken ct = default) {
        var profiles = await _psycheProfileRepository.LoadAsync(ct);
        var id = string.IsNullOrWhiteSpace(npcId) ? profiles.DefaultNpcId : npcId;
        await _psycheStore.DeleteAsync(id, ct);
        var profile = profiles.GetRequiredProfile(id);
        return await _psycheOrchestrator.LoadOrCreateAsync(id, profile, _psycheStore, ct);
    }

    public async Task<IReadOnlyList<string>> GetAvailableSessionIdsAsync(CancellationToken ct) {
        await Task.Yield();
        var sessionsDir = AppPaths.SessionsDir;
        if (!Directory.Exists(sessionsDir)) return Array.Empty<string>();

        return Directory.GetFiles(sessionsDir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    public async Task<IReadOnlyList<string>> GetAvailableNpcIdsAsync(CancellationToken ct) {
        var profiles = await _profileRepository.LoadAsync(ct);
        return profiles.Npcs.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task SwitchSessionAsync(string sessionId, CancellationToken ct) {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        CurrentSessionId = sessionId.Trim();
        var profiles = await _profileRepository.LoadAsync(ct);
        var state = await _store.LoadAsync(CurrentSessionId, ct);
        if (string.IsNullOrWhiteSpace(state.NpcId)) {
            state.NpcId = profiles.DefaultNpcId;
            await _store.SaveAsync(state, ct);
        }

        var currentNpcId = string.IsNullOrWhiteSpace(state.NpcId) ? profiles.DefaultNpcId : state.NpcId;
        _currentNpcDisplayName = profiles.GetRequiredProfile(currentNpcId).DisplayName;
    }

    public async Task<string> GetCurrentNpcIdAsync(CancellationToken ct) {
        var profiles = await _profileRepository.LoadAsync(ct);
        var state = await _store.LoadAsync(CurrentSessionId, ct);
        return string.IsNullOrWhiteSpace(state.NpcId) ? profiles.DefaultNpcId : state.NpcId;
    }

    public async Task<SceneJudgeResult> RecommendSceneAsync(string userText, CancellationToken ct) {
        try {
            if (!await TryInitializeAsync()) {
                return SceneJudgeResult.NoChange;
            }

            var currentSession = await _store.LoadAsync(CurrentSessionId, ct);
            var profiles = await _profileRepository.LoadAsync(ct);
            var psycheProfiles = await _psycheProfileRepository.LoadAsync(ct);
            var npcId = string.IsNullOrWhiteSpace(currentSession.NpcId) ? profiles.DefaultNpcId : currentSession.NpcId;
            var profile = profiles.GetRequiredProfile(npcId);
            var psycheProfile = psycheProfiles.GetRequiredProfile(npcId);

            var affinityEngine = new AffinityEngine("gpt-5.1", _activeApiKey ?? string.Empty);
            var affinity = await affinityEngine.LoadOrCreateAsync(npcId, profile, _affinityStore, ct);
            var psyche = await _psycheOrchestrator.LoadOrCreateAsync(npcId, psycheProfile, _psycheStore, ct);

            var assets = AssetScanner.Scan();
            if (assets.BgmFiles.Count == 0 && assets.BackgroundFiles.Count == 0) {
                return SceneJudgeResult.NoChange;
            }

            var recentContext = BuildRecentContext(currentSession.Turns, 6);
            var narrated = _stateNarrator.BuildJudgeStateText(npcId, psycheProfile, affinity, psyche, userText, recentContext);
            return await _sceneJudge!.EvaluateAsync(userText, profile.DisplayName, recentContext, narrated, assets.BgmFiles, assets.BackgroundFiles, ct);
        }
        catch (Exception ex) {
            Log.Error(ex, "RecommendSceneAsync");
            return SceneJudgeResult.NoChange;
        }
    }

    public async Task SetCurrentNpcAsync(string npcId, CancellationToken ct) {
        if (string.IsNullOrWhiteSpace(npcId)) return;
        var profiles = await _profileRepository.LoadAsync(ct);
        var state = await _store.LoadAsync(CurrentSessionId, ct);
        state.NpcId = npcId.Trim();
        await _store.SaveAsync(state, ct);
        var profile = profiles.GetRequiredProfile(state.NpcId);
        _currentNpcDisplayName = profile.DisplayName;
        var affinityEngine = new AffinityEngine("gpt-5.1", _activeApiKey ?? string.Empty);
        await affinityEngine.LoadOrCreateAsync(state.NpcId, profile, _affinityStore, ct);
    }

    private static void TryEnsureProfileCopy(string primaryPath, string fallbackPath) {
        try {
            if (File.Exists(primaryPath)) return;
            if (!File.Exists(fallbackPath)) return;
            File.Copy(fallbackPath, primaryPath, overwrite: false);
        }
        catch (Exception ex) {
            Log.Error(ex, "TryEnsureProfileCopy");
        }
    }

    private static string BuildPsycheSummary(string npcId, PsycheProfileConfig profile, AffinityState affinity, PsycheState psyche, string recentContext) {
        var sb = new StringBuilder();
        sb.AppendLine($"心理状態: {npcId}");
        sb.AppendLine($"現在ムード    : valence={psyche.Mood.CurrentValence:F1}, arousal={psyche.Mood.CurrentArousal:F1}, control={psyche.Mood.CurrentControl:F1}");
        sb.AppendLine($"基準ムード    : valence={psyche.Mood.BaselineValence:F1}, arousal={psyche.Mood.BaselineArousal:F1}, control={psyche.Mood.BaselineControl:F1}");

        var desireTop = Enum.GetValues<DesireAxis>()
            .Select(axis => new { Axis = axis, Effective = PsycheOrchestrator.Effective(GetOrZero(psyche.DesireTrait, axis), GetOrZero(psyche.DesireDeficit, axis)) })
            .OrderByDescending(x => x.Effective)
            .Take(profile.K.KDesire)
            .ToList();

        sb.AppendLine($"Desire 上位{profile.K.KDesire}（effective = trait + deficit）:");
        foreach (var item in desireTop) {
            sb.AppendLine($"- {item.Axis}: {item.Effective:F1}");
        }

        var trustOk = affinity.Trust >= profile.LibidoGate.MinTrust;
        var hateOk = affinity.Hate < profile.LibidoGate.MaxHate;
        var keywordHit = profile.LibidoGate.SexualKeywords.Any(k => recentContext.Contains(k, StringComparison.OrdinalIgnoreCase));
        var gated = !StateNarrator.EvaluateLibidoGate(profile, affinity, string.Empty, recentContext);
        sb.AppendLine($"Libidoゲート: {(gated ? "閉" : "開")} (trust {affinity.Trust:F1}/{profile.LibidoGate.MinTrust:F1}, hate {affinity.Hate:F1}/{profile.LibidoGate.MaxHate:F1}, keywordHit={keywordHit}, trustOk={trustOk}, hateOk={hateOk})");
        if (!gated) {
            var libidoTop = Enum.GetValues<LibidoAxis>()
                .Select(axis => new { Axis = axis, Effective = PsycheOrchestrator.Effective(GetOrZero(psyche.Libido.Trait, axis), GetOrZero(psyche.Libido.Deficit, axis)) })
                .OrderByDescending(x => x.Effective)
                .Take(profile.K.KLibido)
                .ToList();

            sb.AppendLine($"Libido 上位{profile.K.KLibido}（effective = trait + deficit）:");
            foreach (var item in libidoTop) {
                sb.AppendLine($"- {item.Axis}: {item.Effective:F1}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static double GetOrZero<T>(Dictionary<T, double> map, T key) where T : notnull => map.TryGetValue(key, out var value) ? value : 0;


    private static string EnsureTrailingNewline(string text) {
        if (string.IsNullOrEmpty(text)) {
            return "\n";
        }

        return text.EndsWith('\n') ? text : text + '\n';
    }
    private static bool TryParseCommand(string input, out string cmd, out string arg) {
        cmd = string.Empty;
        arg = string.Empty;
        if (string.IsNullOrWhiteSpace(input) || !input.StartsWith('/')) return false;

        var body = input[1..].Trim();
        if (body.Length == 0) return false;

        var split = body.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        cmd = split[0];
        arg = split.Length > 1 ? split[1] : string.Empty;
        return true;
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

    private sealed record CommandResult(bool Handled, string? NextSessionId);
}
