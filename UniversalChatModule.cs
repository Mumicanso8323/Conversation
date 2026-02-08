namespace Conversation;

#nullable enable
#pragma warning disable OPENAI001 // 種類は、評価の目的でのみ提供されています。将来の更新で変更または削除されることがあります。続行するには、この診断を非表示にします。

using System.Text.Json;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;

public enum ChatEngineMode {
    ResponsesChained,        // previous_response_id を保持して継続
    ResponsesManualHistory,  // ResponseItem 履歴を毎回送る
    ChatCompletions          // ChatClient + ChatTool で関数呼び出し
}

public sealed record ChatModuleOptions(
    string Model,
    string ApiKey,
    string? SystemInstructions = null,
    ChatEngineMode Mode = ChatEngineMode.ResponsesChained,
    bool Streaming = true,
    int MaxToolCallRounds = 8
);

public sealed class ChatSessionState {
    public string SessionId { get; init; } = Guid.NewGuid().ToString("n");

    // ResponsesChained 用
    public string? PreviousResponseId { get; set; }

    // ResponsesManualHistory / ChatCompletions 用（必要最低限）
    public List<ResponseItem> ResponseHistory { get; } = new();
    public List<ChatMessage> ChatHistory { get; } = new();
}

public interface IChatStateStore {
    Task<ChatSessionState> LoadAsync(string sessionId, CancellationToken ct);
    Task SaveAsync(ChatSessionState state, CancellationToken ct);
    Task DeleteAsync(string sessionId, CancellationToken ct);
}

/// <summary>
/// とりあえず動くインメモリ。実運用では JSON/File/DB に差し替え。
/// </summary>
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

public sealed class UniversalChatModule {
    private readonly ChatModuleOptions _opt;
    private readonly IChatStateStore _store;

    private readonly ResponsesClient _responses;
    private readonly ChatClient _chat;

    // Tools (Responses)
    private readonly List<ResponseTool> _responseTools = new();
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<string>>> _responseToolHandlers = new();

    // Tools (ChatCompletions)
    private readonly List<ChatTool> _chatTools = new();
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<string>>> _chatToolHandlers = new();

    public UniversalChatModule(ChatModuleOptions opt, IChatStateStore store) {
        _opt = opt;
        _store = store;

        _responses = new ResponsesClient(opt.Model, opt.ApiKey);
        _chat = new ChatClient(opt.Model, opt.ApiKey);
    }

    /// <summary>
    /// JSON Schema で引数を定義し、C# 側のハンドラを登録する（Responses用）。
    /// </summary>
    public UniversalChatModule AddResponseFunctionTool(
        string name,
        string description,
        string jsonSchema,
        Func<JsonElement, CancellationToken, Task<string>> handler,
        bool strictModeEnabled = false) {
        // ResponseTool.CreateFunctionTool はSDKバージョンにより存在しない可能性があるため、
        // その場合は ChatCompletions モードを使うか、SDK更新を推奨。
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

    /// <summary>
    /// JSON Schema で引数を定義し、C# 側のハンドラを登録する（ChatCompletions用）。
    /// </summary>
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

    /// <summary>
    /// 1ターン実行（非ストリーミング）。
    /// ストリーミング版は SendStreamingAsync を使う。
    /// </summary>
    public async Task<string> SendAsync(string sessionId, string userText, CancellationToken ct = default) {
        return _opt.Mode switch {
            ChatEngineMode.ResponsesChained or ChatEngineMode.ResponsesManualHistory
                => await SendWithResponsesAsync(sessionId, userText, streaming: false, ct),
            ChatEngineMode.ChatCompletions
                => await SendWithChatCompletionsAsync(sessionId, userText, streaming: false, ct),
            _ => throw new NotSupportedException()
        };
    }

    /// <summary>
    /// 1ターン実行（ストリーミング）。
    /// Console で “贅沢” をやるならこちら推奨。
    /// </summary>
    public async IAsyncEnumerable<string> SendStreamingAsync(string sessionId, string userText, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default) {
        if (!_opt.Streaming) {
            yield return await SendAsync(sessionId, userText, ct);
            yield break;
        }

        switch (_opt.Mode) {
            case ChatEngineMode.ResponsesChained:
            case ChatEngineMode.ResponsesManualHistory:
                await foreach (var chunk in SendWithResponsesStreamingAsync(sessionId, userText, ct))
                    yield return chunk;
                yield break;

            case ChatEngineMode.ChatCompletions:
                await foreach (var chunk in SendWithChatCompletionsStreamingAsync(sessionId, userText, ct))
                    yield return chunk;
                yield break;

            default:
                throw new NotSupportedException();
        }
    }

    // ---------------------------
    // Responses
    // ---------------------------
    private async Task<string> SendWithResponsesAsync(string sessionId, string userText, bool streaming, CancellationToken ct) {
        var state = await _store.LoadAsync(sessionId, ct);

        // ツール呼び出しがあると複数ラウンドになるので、最大回数で打ち切り
        for (int round = 0; round < _opt.MaxToolCallRounds; round++) {
            var options = BuildResponseOptions(state, userText, streamingEnabled: false);

            ResponseResult resp = await _responses.CreateResponseAsync(options, ct);

            // Chained の場合、次回のために previous_response_id を更新
            if (_opt.Mode == ChatEngineMode.ResponsesChained)
                state.PreviousResponseId = resp.Id;

            // 出力を見て、関数呼び出しがあれば解決して次ラウンド
            var toolOutputs = await ResolveResponseFunctionCallsAsync(resp, ct);
            if (toolOutputs.Count > 0) {
                // tool outputs を履歴に積む（ManualHistory時）
                if (_opt.Mode == ChatEngineMode.ResponsesManualHistory)
                    state.ResponseHistory.AddRange(toolOutputs);

                // 次のラウンドでは userText は空にして「ツール結果を踏まえて続き」を促す
                userText = "（上記ツール結果を踏まえて続けて）";
                await _store.SaveAsync(state, ct);
                continue;
            }

            // 通常メッセージを抽出
            string text = ExtractResponseText(resp);
            // ManualHistoryなら履歴に積む
            if (_opt.Mode == ChatEngineMode.ResponsesManualHistory) {
                state.ResponseHistory.Add(ResponseItem.CreateUserMessageItem(userText));
                // assistant 側は ResponseResult から完全に再構築するのが理想だが、
                // ここでは簡易に “assistant text” のみ積む
                state.ResponseHistory.Add(ResponseItem.CreateAssistantMessageItem(text));
            }

            await _store.SaveAsync(state, ct);
            return text;
        }

        throw new InvalidOperationException("Tool call rounds exceeded.");
    }

    private async IAsyncEnumerable<string> SendWithResponsesStreamingAsync(string sessionId, string userText, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) {
        var state = await _store.LoadAsync(sessionId, ct);

        // streaming でも、ツール呼び出しが絡むと “途中で止めて次ラウンド” が要る。
        // ここでは「ツールなし or ツールが起きなかった」ケースをまず快適にする実装。
        var options = BuildResponseOptions(state, userText, streamingEnabled: true);

        await foreach (StreamingResponseUpdate update in _responses.CreateResponseStreamingAsync(options, ct)) {
            if (update is StreamingResponseOutputTextDeltaUpdate delta)
                yield return delta.Delta;
        }

        // NOTE:
        // ツール呼び出しまで完全にストリーミングで処理する場合は、
        // StreamingResponseUpdate の item done を集約して FunctionCallResponseItem を解決→追いリクエスト…が必要。
        // まず “贅沢な会話UI” を作るなら、ツール呼び出しは非ストリーミング実装から固めるのが速いです。
    }

    private CreateResponseOptions BuildResponseOptions(ChatSessionState state, string userText, bool streamingEnabled) {
        var opt = new CreateResponseOptions {
            StreamingEnabled = streamingEnabled,
        };

        if (!string.IsNullOrWhiteSpace(_opt.SystemInstructions))
            opt.Instructions = _opt.SystemInstructions;

        if (_responseTools.Count > 0)
            foreach (var t in _responseTools)
                opt.Tools.Add(t);

        // (A) ManualHistory：履歴を全部(または必要分) input_items に詰める
        if (_opt.Mode == ChatEngineMode.ResponsesManualHistory) {
            foreach (var item in state.ResponseHistory)
                opt.InputItems.Add(item);

            opt.InputItems.Add(ResponseItem.CreateUserMessageItem(userText));
            return opt;
        }

        // (B) Chained：previous_response_id + 今回のユーザー入力だけ
        if (_opt.Mode == ChatEngineMode.ResponsesChained && state.PreviousResponseId is not null)
            opt.PreviousResponseId = state.PreviousResponseId;

        opt.InputItems.Add(ResponseItem.CreateUserMessageItem(userText));
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

                // ツール結果を ResponseItem にして返す（SDK側 factory が存在する想定）
                outputs.Add(ResponseItem.CreateFunctionCallOutputItem(fc.CallId, result));
            }
        }

        return outputs;
    }

    private static string ExtractResponseText(ResponseResult resp) {
        // README例では MessageResponseItem の Content から Text を取っている :contentReference[oaicite:14]{index=14}
        var parts = new List<string>();
        foreach (var item in resp.OutputItems) {
            if (item is MessageResponseItem msg) {
                var t = msg.Content?.FirstOrDefault()?.Text;
                if (!string.IsNullOrWhiteSpace(t)) parts.Add(t!);
            }
        }
        return string.Join("\n", parts);
    }

    // ---------------------------
    // Chat Completions (ChatClient)
    // ---------------------------
    private async Task<string> SendWithChatCompletionsAsync(string sessionId, string userText, bool streaming, CancellationToken ct) {
        var state = await _store.LoadAsync(sessionId, ct);

        if (state.ChatHistory.Count == 0 && !string.IsNullOrWhiteSpace(_opt.SystemInstructions))
            state.ChatHistory.Add(new SystemChatMessage(_opt.SystemInstructions));

        state.ChatHistory.Add(new UserChatMessage(userText));

        var options = new ChatCompletionOptions();
        if (_chatTools.Count > 0)
            foreach (var t in _chatTools)
                options.Tools.Add(t);

        bool requiresAction;
        do {
            requiresAction = false;

            ChatCompletion completion = await _chat.CompleteChatAsync(state.ChatHistory, options, ct);

            switch (completion.FinishReason) {
                case ChatFinishReason.Stop:
                    state.ChatHistory.Add(new AssistantChatMessage(completion));
                    await _store.SaveAsync(state, ct);
                    return completion.Content[0].Text;

                case ChatFinishReason.ToolCalls:
                    requiresAction = true;
                    state.ChatHistory.Add(new AssistantChatMessage(completion));

                    foreach (ChatToolCall toolCall in completion.ToolCalls) {
                        if (!_chatToolHandlers.TryGetValue(toolCall.FunctionName, out var handler))
                            throw new InvalidOperationException($"No handler registered for tool: {toolCall.FunctionName}");

                        using var doc = JsonDocument.Parse(toolCall.FunctionArguments);
                        string toolResult = await handler(doc.RootElement, ct);

                        // tool message を履歴に追加（READMEの流れ）:contentReference[oaicite:15]{index=15}
                        state.ChatHistory.Add(new ToolChatMessage(toolCall.Id, toolResult));
                    }
                    break;

                default:
                    throw new NotSupportedException($"Unhandled finish reason: {completion.FinishReason}");
            }
        }
        while (requiresAction);

        throw new InvalidOperationException("Unreachable.");
    }

    private async IAsyncEnumerable<string> SendWithChatCompletionsStreamingAsync(string sessionId, string userText, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) {
        // まずは “ツールなしの贅沢ストリーミング” を快適にする版
        var state = await _store.LoadAsync(sessionId, ct);

        if (state.ChatHistory.Count == 0 && !string.IsNullOrWhiteSpace(_opt.SystemInstructions))
            state.ChatHistory.Add(new SystemChatMessage(_opt.SystemInstructions));

        state.ChatHistory.Add(new UserChatMessage(userText));

        var options = new ChatCompletionOptions();
        if (_chatTools.Count > 0)
            foreach (var t in _chatTools)
                options.Tools.Add(t);

        var updates = _chat.CompleteChatStreamingAsync(state.ChatHistory, options, ct);

        await foreach (var u in updates) {
            if (u.ContentUpdate.Count > 0)
                yield return u.ContentUpdate[0].Text;
        }

        // NOTE: streaming + toolcalls を完全対応するなら、
        // toolcalls 更新の集約→tool実行→再度 streaming… が必要。
    }
}
