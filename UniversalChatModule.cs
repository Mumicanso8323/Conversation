namespace Conversation;

using System.IO;

#nullable enable
#pragma warning disable OPENAI001

using System.Text;
using System.Text.Json;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;

public enum ChatEngineMode {
    ResponsesChained,
    ResponsesManualHistory,
    ChatCompletions
}

public sealed record ChatModuleOptions(
    string Model,
    string ApiKey,
    string? SystemInstructions = null,
    ChatEngineMode Mode = ChatEngineMode.ChatCompletions,
    bool Streaming = true,
    int MaxToolCallRounds = 8,
    int SummaryTriggerTurns = 24,
    int KeepLastTurns = 12,
    string SummaryModel = "gpt-5.2",
    Func<string?, string?>? PersonaSystemResolver = null
);

public sealed record ChatRequestContext(
    string? AdditionalSystemMessage = null,
    string? ForcedAssistantReply = null
);


public sealed class ChatTurn {
    public string Role { get; set; } = "user";
    public string Text { get; set; } = string.Empty;
}

public sealed class ChatSessionState {
    public string SessionId { get; init; } = Guid.NewGuid().ToString("n");
    public string? PreviousResponseId { get; set; }
    public List<ChatTurn> Turns { get; set; } = new();
    public string SummaryMemory { get; set; } = string.Empty;
    public int SummarizedTurnCount { get; set; }
    public string? SystemInstructions { get; set; }
    public string? PersonaId { get; set; }
    public string NpcId { get; set; } = "default";
}

public interface IChatStateStore {
    Task<ChatSessionState> LoadAsync(string sessionId, CancellationToken ct);
    Task SaveAsync(ChatSessionState state, CancellationToken ct);
    Task DeleteAsync(string sessionId, CancellationToken ct);
}

public sealed class InMemoryChatStateStore : IChatStateStore {
    private readonly Dictionary<string, ChatSessionState> _map = new();

    public Task<ChatSessionState> LoadAsync(string sessionId, CancellationToken ct) {
        if (_map.TryGetValue(sessionId, out var s)) return Task.FromResult(s);
        s = new ChatSessionState { SessionId = sessionId };
        _map[sessionId] = s;
        return Task.FromResult(s);
    }

    public Task SaveAsync(ChatSessionState state, CancellationToken ct) {
        _map[state.SessionId] = state;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string sessionId, CancellationToken ct) {
        _map.Remove(sessionId);
        return Task.CompletedTask;
    }
}

public sealed class JsonFileChatStateStore : IChatStateStore {
    private readonly string _directory;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public JsonFileChatStateStore(string directory) {
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    public async Task<ChatSessionState> LoadAsync(string sessionId, CancellationToken ct) {
        var path = GetPath(sessionId);
        if (!File.Exists(path)) return new ChatSessionState { SessionId = sessionId };

        try {
            await using var stream = File.OpenRead(path);
            var state = await JsonSerializer.DeserializeAsync<ChatSessionState>(stream, _jsonOptions, ct);
            if (state is null) return new ChatSessionState { SessionId = sessionId };
            if (state.SessionId != sessionId) state = new ChatSessionState {
                SessionId = sessionId,
                PreviousResponseId = state.PreviousResponseId,
                Turns = state.Turns ?? new List<ChatTurn>(),
                SummaryMemory = state.SummaryMemory ?? string.Empty,
                SummarizedTurnCount = Math.Max(0, state.SummarizedTurnCount),
                SystemInstructions = state.SystemInstructions,
                PersonaId = state.PersonaId,
                NpcId = string.IsNullOrWhiteSpace(state.NpcId) ? "default" : state.NpcId
            };
            return state;
        }
        catch (JsonException) {
            return new ChatSessionState { SessionId = sessionId };
        }
    }

    public async Task SaveAsync(ChatSessionState state, CancellationToken ct) {
        Directory.CreateDirectory(_directory);
        await using var stream = File.Create(GetPath(state.SessionId));
        await JsonSerializer.SerializeAsync(stream, state, _jsonOptions, ct);
    }

    public Task DeleteAsync(string sessionId, CancellationToken ct) {
        var path = GetPath(sessionId);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string GetPath(string sessionId) => Path.Combine(_directory, $"{sessionId}.json");
}

public sealed class UniversalChatModule {
    private readonly ChatModuleOptions _opt;
    private readonly IChatStateStore _store;

    private readonly ResponsesClient _responses;
    private readonly ChatClient _chat;
    private readonly ChatClient _summaryChat;

    private readonly List<ResponseTool> _responseTools = new();
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<string>>> _responseToolHandlers = new();

    private readonly List<ChatTool> _chatTools = new();
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<string>>> _chatToolHandlers = new();

    public UniversalChatModule(ChatModuleOptions opt, IChatStateStore store) {
        _opt = opt;
        _store = store;

        _responses = new ResponsesClient(opt.Model, opt.ApiKey);
        _chat = new ChatClient(opt.Model, opt.ApiKey);
        _summaryChat = new ChatClient(opt.SummaryModel, opt.ApiKey);
    }

    public UniversalChatModule AddResponseFunctionTool(
        string name,
        string description,
        string jsonSchema,
        Func<JsonElement, CancellationToken, Task<string>> handler,
        bool strictModeEnabled = false) {
        var tool = ResponseTool.CreateFunctionTool(
            functionName: name,
            functionDescription: description,
            functionParameters: BinaryData.FromString(jsonSchema),
            strictModeEnabled: strictModeEnabled
        );

        _responseTools.Add(tool);
        _responseToolHandlers[name] = handler;
        return this;
    }

    public UniversalChatModule AddChatFunctionTool(
        string name,
        string description,
        string? jsonSchema,
        Func<JsonElement, CancellationToken, Task<string>> handler) {
        ChatTool tool = jsonSchema is null
            ? ChatTool.CreateFunctionTool(name, description)
            : ChatTool.CreateFunctionTool(
                functionName: name,
                functionDescription: description,
                functionParameters: BinaryData.FromString(jsonSchema)
            );

        _chatTools.Add(tool);
        _chatToolHandlers[name] = handler;
        return this;
    }

    public Task ResetAsync(string sessionId, CancellationToken ct = default)
        => _store.DeleteAsync(sessionId, ct);

    public async Task<string> SendAsync(string sessionId, string userText, ChatRequestContext? context = null, CancellationToken ct = default) {
        return _opt.Mode switch {
            ChatEngineMode.ResponsesChained or ChatEngineMode.ResponsesManualHistory
                => await SendWithResponsesAsync(sessionId, userText, context, ct),
            ChatEngineMode.ChatCompletions
                => await SendWithChatCompletionsAsync(sessionId, userText, context, ct),
            _ => throw new NotSupportedException()
        };
    }

    public async IAsyncEnumerable<string> SendStreamingAsync(string sessionId, string userText, ChatRequestContext? context = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default) {
        if (!_opt.Streaming) {
            yield return await SendAsync(sessionId, userText, context, ct);
            yield break;
        }

        switch (_opt.Mode) {
            case ChatEngineMode.ResponsesChained:
            case ChatEngineMode.ResponsesManualHistory:
                if (_responseTools.Count > 0) {
                    yield return "ツール使用時はストリーミングを無効化しました。\n\n";
                    yield return await SendWithResponsesAsync(sessionId, userText, context, ct);
                    yield break;
                }

                await foreach (var chunk in SendWithResponsesStreamingAsync(sessionId, userText, context, ct))
                    yield return chunk;
                yield break;

            case ChatEngineMode.ChatCompletions:
                await foreach (var chunk in SendWithChatCompletionsStreamingAsync(sessionId, userText, context, ct))
                    yield return chunk;
                yield break;

            default:
                throw new NotSupportedException();
        }
    }

    private async Task<string> SendWithResponsesAsync(string sessionId, string userText, ChatRequestContext? context, CancellationToken ct) {
        var state = await _store.LoadAsync(sessionId, ct);
        await MaybeSummarizeAsync(state, ct);

        if (!string.IsNullOrWhiteSpace(context?.ForcedAssistantReply)) {
            var forcedReply = context.ForcedAssistantReply!;
            state.Turns.Add(new ChatTurn { Role = "user", Text = userText });
            state.Turns.Add(new ChatTurn { Role = "assistant", Text = forcedReply });
            await _store.SaveAsync(state, ct);
            return forcedReply;
        }

        var requestText = userText;
        IReadOnlyList<ResponseItem>? pendingToolOutputs = null;
        for (int round = 0; round < _opt.MaxToolCallRounds; round++) {
            var options = BuildResponseOptions(state, requestText, context, streamingEnabled: false, pendingToolOutputs);

            ResponseResult resp = await _responses.CreateResponseAsync(options, ct);
            if (_opt.Mode == ChatEngineMode.ResponsesChained)
                state.PreviousResponseId = resp.Id;

            var toolOutputs = await ResolveResponseFunctionCallsAsync(resp, ct);
            if (toolOutputs.Count > 0) {
                pendingToolOutputs = toolOutputs;
                requestText = "（上記ツール結果を踏まえて続けて）";
                continue;
            }

            pendingToolOutputs = null;

            string text = ExtractResponseText(resp);
            state.Turns.Add(new ChatTurn { Role = "user", Text = userText });
            state.Turns.Add(new ChatTurn { Role = "assistant", Text = text });
            await _store.SaveAsync(state, ct);
            return text;
        }

        throw new InvalidOperationException("Tool call rounds exceeded.");
    }

    private async IAsyncEnumerable<string> SendWithResponsesStreamingAsync(string sessionId, string userText, ChatRequestContext? context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) {
        var state = await _store.LoadAsync(sessionId, ct);
        await MaybeSummarizeAsync(state, ct);

        if (!string.IsNullOrWhiteSpace(context?.ForcedAssistantReply)) {
            var forcedReply = context.ForcedAssistantReply!;
            state.Turns.Add(new ChatTurn { Role = "user", Text = userText });
            state.Turns.Add(new ChatTurn { Role = "assistant", Text = forcedReply });
            await _store.SaveAsync(state, ct);
            yield return forcedReply;
            yield break;
        }

        var options = BuildResponseOptions(state, userText, context, streamingEnabled: true);
        var assistantText = new StringBuilder();

        await foreach (StreamingResponseUpdate update in _responses.CreateResponseStreamingAsync(options, ct)) {
            if (update is StreamingResponseOutputTextDeltaUpdate delta) {
                assistantText.Append(delta.Delta);
                yield return delta.Delta;
            }
        }

        state.Turns.Add(new ChatTurn { Role = "user", Text = userText });
        state.Turns.Add(new ChatTurn { Role = "assistant", Text = assistantText.ToString() });
        await _store.SaveAsync(state, ct);
    }

    private CreateResponseOptions BuildResponseOptions(ChatSessionState state, string userText, ChatRequestContext? context, bool streamingEnabled, IReadOnlyList<ResponseItem>? toolOutputs = null) {
        var opt = new CreateResponseOptions {
            StreamingEnabled = streamingEnabled,
        };

        var instructions = BuildInstructionBlocks(state, context);
        if (!string.IsNullOrWhiteSpace(instructions))
            opt.Instructions = instructions;

        if (_responseTools.Count > 0)
            foreach (var t in _responseTools)
                opt.Tools.Add(t);

        if (_opt.Mode == ChatEngineMode.ResponsesManualHistory) {
            int keepLastTurns = Math.Max(1, _opt.KeepLastTurns);
            foreach (var turn in state.Turns.Skip(Math.Max(0, state.Turns.Count - keepLastTurns))) {
                if (turn.Role == "assistant") opt.InputItems.Add(ResponseItem.CreateAssistantMessageItem(turn.Text));
                else opt.InputItems.Add(ResponseItem.CreateUserMessageItem(turn.Text));
            }

            opt.InputItems.Add(ResponseItem.CreateUserMessageItem(userText));
            if (toolOutputs is not null) {
                foreach (var output in toolOutputs) {
                    opt.InputItems.Add(output);
                }
            }
            return opt;
        }

        if (_opt.Mode == ChatEngineMode.ResponsesChained && state.PreviousResponseId is not null)
            opt.PreviousResponseId = state.PreviousResponseId;

        opt.InputItems.Add(ResponseItem.CreateUserMessageItem(userText));
        if (toolOutputs is not null) {
            foreach (var output in toolOutputs) {
                opt.InputItems.Add(output);
            }
        }
        return opt;
    }

    private async Task<List<ResponseItem>> ResolveResponseFunctionCallsAsync(ResponseResult resp, CancellationToken ct) {
        var outputs = new List<ResponseItem>();

        foreach (var item in resp.OutputItems) {
            if (item is FunctionCallResponseItem fc) {
                if (!_responseToolHandlers.TryGetValue(fc.FunctionName, out var handler))
                    throw new InvalidOperationException($"No handler registered for function: {fc.FunctionName}");

                using var doc = JsonDocument.Parse(fc.FunctionArguments.ToString());
                string result = await handler(doc.RootElement, ct);
                outputs.Add(ResponseItem.CreateFunctionCallOutputItem(fc.CallId, result));
            }
        }

        return outputs;
    }

    private static string ExtractResponseText(ResponseResult resp) {
        var parts = new List<string>();
        foreach (var item in resp.OutputItems) {
            if (item is MessageResponseItem msg) {
                var t = msg.Content?.FirstOrDefault()?.Text;
                if (!string.IsNullOrWhiteSpace(t)) parts.Add(t!);
            }
        }
        return string.Join("\n", parts);
    }

    private async Task<string> SendWithChatCompletionsAsync(string sessionId, string userText, ChatRequestContext? context, CancellationToken ct) {
        var state = await _store.LoadAsync(sessionId, ct);
        await MaybeSummarizeAsync(state, ct);

        if (!string.IsNullOrWhiteSpace(context?.ForcedAssistantReply)) {
            var forcedReply = context.ForcedAssistantReply!;
            state.Turns.Add(new ChatTurn { Role = "user", Text = userText });
            state.Turns.Add(new ChatTurn { Role = "assistant", Text = forcedReply });
            await _store.SaveAsync(state, ct);
            return forcedReply;
        }

        var options = new ChatCompletionOptions();
        if (_chatTools.Count > 0)
            foreach (var t in _chatTools)
                options.Tools.Add(t);

        var messages = BuildMessagesForModel(state, userText, context);

        bool requiresAction;
        string lastAssistantText = string.Empty;
        do {
            requiresAction = false;
            ChatCompletion completion = await _chat.CompleteChatAsync(messages, options, ct);

            switch (completion.FinishReason) {
                case ChatFinishReason.Stop:
                    lastAssistantText = string.Join("\n", completion.Content.Select(c => c.Text));
                    messages.Add(new AssistantChatMessage(completion));
                    break;

                case ChatFinishReason.ToolCalls:
                    requiresAction = true;
                    messages.Add(new AssistantChatMessage(completion));

                    foreach (ChatToolCall toolCall in completion.ToolCalls) {
                        if (!_chatToolHandlers.TryGetValue(toolCall.FunctionName, out var handler))
                            throw new InvalidOperationException($"No handler registered for tool: {toolCall.FunctionName}");

                        using var doc = JsonDocument.Parse(toolCall.FunctionArguments);
                        string toolResult = await handler(doc.RootElement, ct);
                        messages.Add(new ToolChatMessage(toolCall.Id, toolResult));
                    }
                    break;

                default:
                    throw new NotSupportedException($"Unhandled finish reason: {completion.FinishReason}");
            }
        }
        while (requiresAction);

        state.Turns.Add(new ChatTurn { Role = "user", Text = userText });
        state.Turns.Add(new ChatTurn { Role = "assistant", Text = lastAssistantText });
        await _store.SaveAsync(state, ct);

        return lastAssistantText;
    }

    private async IAsyncEnumerable<string> SendWithChatCompletionsStreamingAsync(string sessionId, string userText, ChatRequestContext? context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default) {
        var state = await _store.LoadAsync(sessionId, ct);
        await MaybeSummarizeAsync(state, ct);

        if (!string.IsNullOrWhiteSpace(context?.ForcedAssistantReply)) {
            var forcedReply = context.ForcedAssistantReply!;
            state.Turns.Add(new ChatTurn { Role = "user", Text = userText });
            state.Turns.Add(new ChatTurn { Role = "assistant", Text = forcedReply });
            await _store.SaveAsync(state, ct);
            yield return forcedReply;
            yield break;
        }

        var options = new ChatCompletionOptions();
        if (_chatTools.Count > 0)
            foreach (var t in _chatTools)
                options.Tools.Add(t);

        var messages = BuildMessagesForModel(state, userText, context);
        var updates = _chat.CompleteChatStreamingAsync(messages, options, ct);

        var sb = new StringBuilder();
        await foreach (var u in updates) {
            if (u.ContentUpdate.Count > 0) {
                var chunk = u.ContentUpdate[0].Text;
                sb.Append(chunk);
                yield return chunk;
            }
        }

        state.Turns.Add(new ChatTurn { Role = "user", Text = userText });
        state.Turns.Add(new ChatTurn { Role = "assistant", Text = sb.ToString() });
        await _store.SaveAsync(state, ct);
    }

    private List<ChatMessage> BuildMessagesForModel(ChatSessionState state, string userText, ChatRequestContext? context) {
        var messages = new List<ChatMessage>();
        var instructions = BuildInstructionBlocks(state, context);

        if (!string.IsNullOrWhiteSpace(instructions))
            messages.Add(new SystemChatMessage(instructions));

        int keepLastTurns = Math.Max(1, _opt.KeepLastTurns);
        foreach (var turn in state.Turns.Skip(Math.Max(0, state.Turns.Count - keepLastTurns)))
            messages.Add(ToChatMessage(turn));

        messages.Add(new UserChatMessage(userText));
        return messages;
    }

    private string BuildInstructionBlocks(ChatSessionState state, ChatRequestContext? context) {
        var blocks = new List<string>();
        var systemInstructions = ResolveSystemInstructions(state);
        if (!string.IsNullOrWhiteSpace(systemInstructions)) {
            blocks.Add(systemInstructions);
        }

        if (!string.IsNullOrWhiteSpace(state.SummaryMemory)) {
            blocks.Add($"[MEMORY]\n{state.SummaryMemory}");
        }

        if (!string.IsNullOrWhiteSpace(context?.AdditionalSystemMessage)) {
            blocks.Add(context.AdditionalSystemMessage);
        }

        return string.Join("\n\n", blocks);
    }

    private string? ResolveSystemInstructions(ChatSessionState state) {
        var fromPersona = _opt.PersonaSystemResolver?.Invoke(state.PersonaId);
        if (!string.IsNullOrWhiteSpace(fromPersona)) {
            return fromPersona;
        }

        return state.SystemInstructions ?? _opt.SystemInstructions;
    }

    private async Task MaybeSummarizeAsync(ChatSessionState state, CancellationToken ct) {
        int triggerTurns = Math.Max(1, _opt.SummaryTriggerTurns);
        int keepLastTurns = Math.Max(1, _opt.KeepLastTurns);

        int eligibleTurns = Math.Max(0, state.Turns.Count - keepLastTurns);
        int summarizedTurns = Math.Clamp(state.SummarizedTurnCount, 0, eligibleTurns);
        int turnsToSummarize = eligibleTurns - summarizedTurns;
        if (turnsToSummarize < triggerTurns)
            return;

        var cut = state.Turns.Skip(summarizedTurns).Take(turnsToSummarize).ToList();
        var cutText = string.Join("\n", cut.Select(t => $"{t.Role}: {t.Text}"));

        var summarizeMessages = new List<ChatMessage> {
            new SystemChatMessage("あなたは会話ログ要約係です。事実・関係・好み・未解決事項を短い箇条書きで出力し、感情描写は省略してください。"),
            new UserChatMessage(cutText)
        };

        ChatCompletion summaryCompletion = await _summaryChat.CompleteChatAsync(summarizeMessages, new ChatCompletionOptions(), ct);
        string summary = string.Join("\n", summaryCompletion.Content.Select(c => c.Text)).Trim();

        if (!string.IsNullOrWhiteSpace(summary)) {
            state.SummaryMemory = string.IsNullOrWhiteSpace(state.SummaryMemory)
                ? summary
                : $"{state.SummaryMemory}\n{summary}";
        }

        state.SummarizedTurnCount = eligibleTurns;
        await _store.SaveAsync(state, ct);
    }

    private static ChatMessage ToChatMessage(ChatTurn turn) {
        return turn.Role switch {
            "assistant" => new AssistantChatMessage(turn.Text),
            "system" => new SystemChatMessage(turn.Text),
            _ => new UserChatMessage(turn.Text)
        };
    }
}
