using System.Text.Json;
using OpenAI.Chat;

namespace Conversation.Psyche;

public sealed class PsycheJudge {
    private readonly ChatClient _chatClient;

    public PsycheJudge(string model, string apiKey) {
        _chatClient = new ChatClient(model, apiKey);
    }

    public async Task<PsycheJudgeResult> EvaluateAsync(string userText, string npcName, string recentContext, string narratedState, CancellationToken ct) {
        var messages = new List<ChatMessage> {
            new SystemChatMessage("""
You are a judge module for roleplay orchestration.
Output JSON only. No markdown.
Never issue persona, style, tone, first-person, or second-person instructions.
Generate only: psyche_delta and directives for content priority/avoid/bounds.
Always set bounds.persona_immutable=true, bounds.no_style_change=true, bounds.sexual_safety_strict=true.
Sexual directives must remain safety-first and non-explicit.
Delta ranges per turn:
- desire_deficit: -1.5..1.5
- libido_deficit: -1.0..1.0
- mood.valence: -2..2
- mood.arousal/control: -1..1
"""),
            new UserChatMessage($"NPC: {npcName}\nUtterance: {userText}\nRecent context:\n{recentContext}\n\nState:\n{narratedState}")
        };

        var completion = await _chatClient.CompleteChatAsync(messages, new ChatCompletionOptions { Temperature = 0.1f }, ct);
        var raw = string.Join("", completion.Value.Content.Select(c => c.Text)).Trim();

        try {
            var parsed = JsonSerializer.Deserialize<PsycheJudgeResult>(raw, PsycheJson.Options);
            return parsed?.Normalize() ?? PsycheJudgeResult.SafeDefault();
        }
        catch (JsonException) {
            return PsycheJudgeResult.SafeDefault();
        }
    }
}

public sealed class PsycheJudgeResult {
    public PsycheDelta PsycheDelta { get; set; } = new();
    public ResponseDirectives Directives { get; set; } = ResponseDirectives.SafeDefault();

    public PsycheJudgeResult Normalize() {
        Directives ??= ResponseDirectives.SafeDefault();
        Directives.Bounds.PersonaImmutable = true;
        Directives.Bounds.NoStyleChange = true;
        Directives.Bounds.SexualSafetyStrict = true;
        PsycheDelta ??= new PsycheDelta();
        return this;
    }

    public static PsycheJudgeResult SafeDefault() => new() {
        PsycheDelta = new PsycheDelta(),
        Directives = ResponseDirectives.SafeDefault()
    };
}

public sealed class PsycheDelta {
    public Dictionary<DesireAxis, double> DesireDeficit { get; set; } = new();
    public MoodDelta Mood { get; set; } = new();
    public Dictionary<LibidoAxis, double> LibidoDeficit { get; set; } = new();
}

public sealed class MoodDelta {
    public double Valence { get; set; }
    public double Arousal { get; set; }
    public double Control { get; set; }
}
