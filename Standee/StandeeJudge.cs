using System.Text.Json;
using OpenAI.Chat;

namespace Conversation.Standee;

public sealed class StandeeJudge {
    private readonly ChatClient _chatClient;

    public StandeeJudge(string model, string apiKey) {
        _chatClient = new ChatClient(model, apiKey);
    }

    public async Task<string> EvaluateAsync(string userText, string npcName, string recentContext, string narratedState, CancellationToken ct) {
        var allowed = string.Join(", ", StandeeSprites.Allowed.Select(x => $"\"{x}\""));
        var messages = new List<ChatMessage> {
            new SystemChatMessage($$"""
You are a strict JSON sprite selector.
Return EXACTLY one JSON object and no extra text.
Schema:
{"sprite":"<filename>"}
Allowed filenames: [{{allowed}}]
If uncertain, choose "{{StandeeSprites.Default}}".
"""),
            new UserChatMessage($"NPC: {npcName}\nUser input: {userText}\nRecent context:\n{recentContext}\n\nNarrated state:\n{narratedState}")
        };

        try {
            var completion = await _chatClient.CompleteChatAsync(messages, new ChatCompletionOptions { Temperature = 0.0f }, ct);
            var raw = string.Concat(completion.Value.Content.Select(c => c.Text)).Trim();
            return ParseSpriteOrDefault(raw);
        }
        catch {
            return StandeeSprites.Default;
        }
    }

    private static string ParseSpriteOrDefault(string raw) {
        if (TryParseSprite(raw, out var sprite)) {
            return sprite;
        }

        var first = raw.IndexOf('{');
        var last = raw.LastIndexOf('}');
        if (first >= 0 && last > first) {
            var candidate = raw[first..(last + 1)];
            if (TryParseSprite(candidate, out sprite)) {
                return sprite;
            }
        }

        return StandeeSprites.Default;
    }

    private static bool TryParseSprite(string jsonText, out string sprite) {
        sprite = StandeeSprites.Default;
        try {
            using var json = JsonDocument.Parse(jsonText);
            if (json.RootElement.ValueKind != JsonValueKind.Object) {
                return false;
            }

            if (!json.RootElement.TryGetProperty("sprite", out var spriteElement) || spriteElement.ValueKind != JsonValueKind.String) {
                return false;
            }

            var parsed = spriteElement.GetString();
            sprite = StandeeSprites.NormalizeOrDefault(parsed);
            return true;
        }
        catch {
            return false;
        }
    }
}
