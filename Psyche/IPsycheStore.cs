namespace Conversation.Psyche;

public interface IPsycheStore {
    Task DeleteAsync(string npcId, CancellationToken none);
    Task<PsycheState?> LoadAsync(string npcId, CancellationToken ct);
    Task SaveAsync(PsycheState state, CancellationToken ct);
}
