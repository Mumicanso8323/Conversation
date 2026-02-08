namespace Conversation.Psyche;

public interface IPsycheStore {
    Task<PsycheState?> LoadAsync(string npcId, CancellationToken ct);
    Task SaveAsync(PsycheState state, CancellationToken ct);
}
