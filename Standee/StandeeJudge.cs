using System.Text.Json;
using OpenAI.Chat;

namespace Conversation.Standee;

public sealed class StandeeJudge {
    private readonly ChatClient _chatClient;

    public StandeeJudge(string model, string apiKey) {
        _chatClient = new ChatClient(model, apiKey);
    }

    public async Task<string> EvaluateAsync(string userText, string npcName, string recentContext, string narratedState, CancellationToken ct) {
        var messages = new List<ChatMessage> {
            new SystemChatMessage($$"""
You are a sprite selector.
Return JSON only. No markdown. No explanation.
Output format must be exactly:
{"sprite":"<filename>"}
The sprite value MUST be one of:
- 普通.png
- 笑顔.png
- 期待、心躍る.png
- ちょっとした不満.png
- 悲しい、困る、憐憫.png
- 恥じらい、照れ、恍惚.png
- 不穏な笑み、たくらみ.png
If uncertain, use 普通.png.
"""),
            new UserChatMessage($"NPC: {npcName}\nUtterance: {userText}\nRecent context:\n{recentContext}\n\nState:\n{narratedState}")
        };

        try {
            var completion = await _chatClient.CompleteChatAsync(messages, new ChatCompletionOptions { Temperature = 0.1f }, ct);
            var raw = string.Join("", completion.Value.Content.Select(c => c.Text)).Trim();
            var parsed = JsonSerializer.Deserialize<StandeeJudgeResponse>(raw, new JsonSerializerOptions {
                PropertyNameCaseInsensitive = true
            });
            return StandeeSprites.Normalize(parsed?.Sprite);
        }
        catch {
            return StandeeSprites.Default;
        }
    }

    private sealed class StandeeJudgeResponse {
        public string? Sprite { get; set; }
    }
}
