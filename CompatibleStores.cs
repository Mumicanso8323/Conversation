namespace Conversation;

using System.IO;
using Conversation.Affinity;
using Conversation.Psyche;

public sealed class CompatibleChatStateStore : IChatStateStore {
    private readonly string _primaryDir;
    private readonly string _fallbackDir;
    private readonly JsonFileChatStateStore _primary;
    private readonly JsonFileChatStateStore _fallback;

    public CompatibleChatStateStore(string primaryDir, string fallbackDir) {
        _primaryDir = primaryDir;
        _fallbackDir = fallbackDir;
        _primary = new JsonFileChatStateStore(primaryDir);
        _fallback = new JsonFileChatStateStore(fallbackDir);
    }

    public async Task<ChatSessionState> LoadAsync(string sessionId, CancellationToken ct) {
        if (File.Exists(Path.Combine(_primaryDir, $"{sessionId}.json"))) {
            return await _primary.LoadAsync(sessionId, ct);
        }

        if (File.Exists(Path.Combine(_fallbackDir, $"{sessionId}.json"))) {
            return await _fallback.LoadAsync(sessionId, ct);
        }

        return await _primary.LoadAsync(sessionId, ct);
    }

    public Task SaveAsync(ChatSessionState state, CancellationToken ct) => _primary.SaveAsync(state, ct);
    public Task DeleteAsync(string sessionId, CancellationToken ct) => _primary.DeleteAsync(sessionId, ct);
}

public sealed class CompatibleAffinityStore : IAffinityStore {
    private readonly string _primaryDir;
    private readonly string _fallbackDir;
    private readonly JsonFileAffinityStore _primary;
    private readonly JsonFileAffinityStore _fallback;

    public CompatibleAffinityStore(string primaryDir, string fallbackDir) {
        _primaryDir = primaryDir;
        _fallbackDir = fallbackDir;
        _primary = new JsonFileAffinityStore(primaryDir);
        _fallback = new JsonFileAffinityStore(fallbackDir);
    }

    public async Task<AffinityState?> LoadAsync(string npcId, CancellationToken ct = default) {
        if (File.Exists(Path.Combine(_primaryDir, $"{npcId}.json"))) {
            return await _primary.LoadAsync(npcId, ct);
        }

        if (File.Exists(Path.Combine(_fallbackDir, $"{npcId}.json"))) {
            return await _fallback.LoadAsync(npcId, ct);
        }

        return await _primary.LoadAsync(npcId, ct);
    }

    public Task SaveAsync(AffinityState state, CancellationToken ct = default) => _primary.SaveAsync(state, ct);

    // 削除メソッドがJsonFileAffinityStoreに存在しないため、ファイルを直接削除する実装に修正
    public Task DeleteAsync(string npcId, CancellationToken ct = default)
    {
        var path = Path.Combine(_primaryDir, $"{npcId}.json");
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }
}

public sealed class CompatiblePsycheStore : IPsycheStore {
    private readonly string _primaryDir;
    private readonly string _fallbackDir;
    private readonly JsonFilePsycheStore _primary;
    private readonly JsonFilePsycheStore _fallback;

    public CompatiblePsycheStore(string primaryDir, string fallbackDir) {
        _primaryDir = primaryDir;
        _fallbackDir = fallbackDir;
        _primary = new JsonFilePsycheStore(primaryDir);
        _fallback = new JsonFilePsycheStore(fallbackDir);
    }

    public async Task<PsycheState?> LoadAsync(string npcId, CancellationToken ct = default) {
        if (File.Exists(Path.Combine(_primaryDir, $"{npcId}.json"))) {
            return await _primary.LoadAsync(npcId, ct);
        }

        if (File.Exists(Path.Combine(_fallbackDir, $"{npcId}.json"))) {
            return await _fallback.LoadAsync(npcId, ct);
        }

        return await _primary.LoadAsync(npcId, ct);
    }

    public Task SaveAsync(PsycheState state, CancellationToken ct = default) => _primary.SaveAsync(state, ct);
    public Task DeleteAsync(string npcId, CancellationToken ct = default) => _primary.DeleteAsync(npcId, ct);
}
