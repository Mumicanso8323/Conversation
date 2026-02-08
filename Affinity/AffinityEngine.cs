using System.Text.Json;
using OpenAI.Chat;

namespace Conversation.Affinity;

public sealed class AffinityEngine {
    private readonly ChatClient _chatClient;

    public AffinityEngine(string model, string apiKey) {
        _chatClient = new ChatClient(model, apiKey);
    }

    public async Task<AffinityState> LoadOrCreateAsync(string npcId, NpcProfile profile, IAffinityStore store, CancellationToken ct) {
        var loaded = await store.LoadAsync(npcId, ct);
        if (loaded is not null) {
            return loaded;
        }

        var created = new AffinityState {
            NpcId = npcId,
            Like = profile.Initial.Like,
            Dislike = profile.Initial.Dislike,
            Liked = profile.Initial.Liked,
            Disliked = profile.Initial.Disliked,
            Love = profile.Initial.Love,
            Hate = profile.Initial.Hate,
            Trust = profile.Initial.Trust,
            Respect = profile.Initial.Respect,
            SexualAwareness = profile.Initial.SexualAwareness,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await store.SaveAsync(created, ct);
        return created;
    }

    public async Task<AffinityDelta> EvaluateDeltaAsync(string userText, string npcName, CancellationToken ct) {
        var messages = new List<ChatMessage> {
            new SystemChatMessage("You score short user text sentiment for a roleplay NPC. Return strict JSON only with numeric fields: like, dislike, trust, respect, sexualAwareness. Range -20..20. No markdown."),
            new UserChatMessage($"NPC: {npcName}\nUtterance: {userText}")
        };

        var completion = await _chatClient.CompleteChatAsync(messages, new ChatCompletionOptions { Temperature = 0.1f }, ct);
        var raw = string.Join("", completion.Content.Select(c => c.Text)).Trim();

        try {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            return new AffinityDelta(
                Get(root, "like"),
                Get(root, "dislike"),
                Get(root, "trust"),
                Get(root, "respect"),
                Get(root, "sexualAwareness")
            );
        }
        catch (JsonException) {
            return new AffinityDelta();
        }
    }

    public void ApplyDelta(AffinityState state, NpcProfile profile, AffinityDelta delta) {
        state.Like = Clamp(state.Like + delta.Like);
        state.Dislike = Clamp(state.Dislike + delta.Dislike);
        state.Trust = Clamp(state.Trust + delta.Trust);
        state.Respect = Clamp(state.Respect + delta.Respect);
        state.SexualAwareness = Clamp(state.SexualAwareness + delta.SexualAwareness);

        state.Liked = Clamp(state.Liked + (state.Like * profile.MemoryGain.LikedFromLike));
        state.Disliked = Clamp(state.Disliked + (state.Dislike * profile.MemoryGain.DislikedFromDislike));

        state.Like = Clamp(state.Like * profile.Decay.Like);
        state.Dislike = Clamp(state.Dislike * profile.Decay.Dislike);

        var balance = state.Liked - state.Disliked;
        if (balance >= 0) {
            state.Love = Clamp(state.Love + (balance / 100.0) * profile.StanceGain.LoveFromBalance);
            state.Hate = Clamp(state.Hate - 0.3);
        }
        else {
            state.Hate = Clamp(state.Hate + (Math.Abs(balance) / 100.0) * profile.StanceGain.HateFromBalance);
            state.Love = Clamp(state.Love - 0.3);
        }

        state.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public string BuildRoleplayStatePrompt(string npcId, NpcProfile profile, AffinityState state) =>
$"""
[ROLEPLAY NPC STATE]
npcId={npcId} name={profile.DisplayName}
like={state.Like:F1} dislike={state.Dislike:F1} liked={state.Liked:F1} disliked={state.Disliked:F1}
love={state.Love:F1} hate={state.Hate:F1} trust={state.Trust:F1} respect={state.Respect:F1} sexualAwareness={state.SexualAwareness:F1}
Rules:
- loveが高いほど好意的に解釈し、多少の失礼を受け流す
- hateが高いほど疑い深く、短文・拒否・会話終了が増える
- 数値を文章に直接書かない（UIで表示するため）。態度・距離感で表現する
- 会話が発生しない/すぐ終わるのは自然。必要なら「今は話したくない」等で打ち切る
""";

    public string? MaybeGenerateBlockedReply(AffinityState state, NpcProfile profile) {
        var control = profile.Conversation;
        var rng = Random.Shared.NextDouble();

        if (state.Hate >= profile.Thresholds.HateOn && state.Trust <= 25) {
            if (rng < control.SilentChanceAtHighHate) {
                return "……（今は話したくない）";
            }
        }

        if (state.Hate >= 50 || state.Disliked > state.Liked) {
            if (rng < control.ShortReplyChanceAtMidHate) {
                return "……そう。";
            }

            if (rng < control.ShortReplyChanceAtMidHate + control.EndConversationChance) {
                return "今日はここまでにして。";
            }
        }

        return null;
    }

    private static double Get(JsonElement root, string name) {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number) {
            return 0;
        }

        return value.GetDouble();
    }

    public static bool TrySet(AffinityState state, string param, double value) {
        value = Clamp(value);
        switch (param.ToLowerInvariant()) {
            case "like": state.Like = value; return true;
            case "dislike": state.Dislike = value; return true;
            case "liked": state.Liked = value; return true;
            case "disliked": state.Disliked = value; return true;
            case "love": state.Love = value; return true;
            case "hate": state.Hate = value; return true;
            case "trust": state.Trust = value; return true;
            case "respect": state.Respect = value; return true;
            case "sexualawareness":
            case "sexual": state.SexualAwareness = value; return true;
            default: return false;
        }
    }

    public static double Clamp(double value) => Math.Max(0, Math.Min(100, value));
}

public readonly record struct AffinityDelta(
    double Like = 0,
    double Dislike = 0,
    double Trust = 0,
    double Respect = 0,
    double SexualAwareness = 0
);
