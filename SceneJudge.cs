namespace Conversation;

using System.Text.Json;
using OpenAI.Chat;

public sealed class SceneJudge {
    private const string DefaultPrompt = """
You are a strict JSON scene selector.
Return EXACTLY one JSON object and no extra text.
Schema:
{"bgm":"<filename-or-empty>","background":"<filename-or-empty>"}
Allowed bgm filenames: [{allowed_bgm}]
Allowed background filenames: [{allowed_background}]
If uncertain, return empty strings for keys to indicate no change.
""";

    private readonly ChatClient _chatClient;
    private readonly Func<string>? _systemPromptProvider;

    public SceneJudge(string model, string apiKey, Func<string>? systemPromptProvider = null) {
        _chatClient = new ChatClient(model, apiKey);
        _systemPromptProvider = systemPromptProvider;
    }

    public async Task<SceneJudgeResult> EvaluateAsync(
        string userText,
        string npcName,
        string recentContext,
        string narratedState,
        IReadOnlyList<string> allowedBgm,
        IReadOnlyList<string> allowedBackground,
        CancellationToken ct) {

        var systemPrompt = _systemPromptProvider?.Invoke();
        if (string.IsNullOrWhiteSpace(systemPrompt)) {
            systemPrompt = DefaultPrompt;
        }

        systemPrompt = systemPrompt
            .Replace("{allowed_bgm}", string.Join(", ", allowedBgm.Select(x => $"\"{x}\"")), StringComparison.Ordinal)
            .Replace("{allowed_background}", string.Join(", ", allowedBackground.Select(x => $"\"{x}\"")), StringComparison.Ordinal);

        var messages = new List<ChatMessage> {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage($"NPC: {npcName}\nUtterance: {userText}\nRecent context:\n{recentContext}\n\nState:\n{narratedState}")
        };

        try {
            var completion = await _chatClient.CompleteChatAsync(messages, new ChatCompletionOptions { Temperature = 0.1f }, ct);
            var raw = string.Concat(completion.Value.Content.Select(c => c.Text)).Trim();
            return Parse(raw, allowedBgm, allowedBackground);
        }
        catch {
            return SceneJudgeResult.NoChange;
        }
    }

    private static SceneJudgeResult Parse(string raw, IReadOnlyList<string> allowedBgm, IReadOnlyList<string> allowedBackground) {
        if (TryParse(raw, allowedBgm, allowedBackground, out var parsed)) {
            return parsed;
        }

        var first = raw.IndexOf('{');
        var last = raw.LastIndexOf('}');
        if (first >= 0 && last > first) {
            var candidate = raw[first..(last + 1)];
            if (TryParse(candidate, allowedBgm, allowedBackground, out parsed)) {
                return parsed;
            }
        }

        return SceneJudgeResult.NoChange;
    }

    private static bool TryParse(string json, IReadOnlyList<string> allowedBgm, IReadOnlyList<string> allowedBackground, out SceneJudgeResult result) {
        result = SceneJudgeResult.NoChange;
        try {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) {
                return false;
            }

            string? bgm = null;
            string? background = null;
            if (doc.RootElement.TryGetProperty("bgm", out var bgmEl) && bgmEl.ValueKind == JsonValueKind.String) {
                bgm = bgmEl.GetString();
            }

            if (doc.RootElement.TryGetProperty("background", out var bgEl) && bgEl.ValueKind == JsonValueKind.String) {
                background = bgEl.GetString();
            }

            var normalizedBgm = NormalizeOrEmpty(bgm, allowedBgm);
            var normalizedBackground = NormalizeOrEmpty(background, allowedBackground);
            result = new SceneJudgeResult(normalizedBgm, normalizedBackground);
            return true;
        }
        catch {
            return false;
        }
    }

    private static string NormalizeOrEmpty(string? value, IReadOnlyList<string> allowed) {
        if (string.IsNullOrWhiteSpace(value)) {
            return string.Empty;
        }

        var hit = allowed.FirstOrDefault(x => string.Equals(x, value.Trim(), StringComparison.OrdinalIgnoreCase));
        return hit ?? string.Empty;
    }
}

public sealed record SceneJudgeResult(string Bgm, string Background) {
    public static SceneJudgeResult NoChange { get; } = new(string.Empty, string.Empty);
    public bool HasBgmChange => !string.IsNullOrWhiteSpace(Bgm);
    public bool HasBackgroundChange => !string.IsNullOrWhiteSpace(Background);
}
