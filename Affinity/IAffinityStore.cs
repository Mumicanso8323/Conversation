namespace Conversation.Affinity;

public interface IAffinityStore {
    Task<AffinityState?> LoadAsync(string npcId, CancellationToken ct);
    Task SaveAsync(AffinityState state, CancellationToken ct);
}
