using System.IO;
using System.Text.Json;

namespace Conversation.Affinity;

public sealed class JsonFileAffinityStore : IAffinityStore {
    private readonly string _directory;
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public JsonFileAffinityStore(string directory) {
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    public async Task<AffinityState?> LoadAsync(string npcId, CancellationToken ct) {
        var path = GetPath(npcId);
        if (!File.Exists(path)) {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<AffinityState>(stream, _options, ct);
    }

    public async Task SaveAsync(AffinityState state, CancellationToken ct) {
        Directory.CreateDirectory(_directory);
        await using var stream = File.Create(GetPath(state.NpcId));
        await JsonSerializer.SerializeAsync(stream, state, _options, ct);
    }

    private string GetPath(string npcId) => Path.Combine(_directory, $"{npcId}.json");
}
