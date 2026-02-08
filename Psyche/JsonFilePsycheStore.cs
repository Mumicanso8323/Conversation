using System.Text.Json;

namespace Conversation.Psyche;

public sealed class JsonFilePsycheStore : IPsycheStore {
    private readonly string _directory;
    private readonly JsonSerializerOptions _options = new() {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public JsonFilePsycheStore(string directory) {
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    public async Task<PsycheState?> LoadAsync(string npcId, CancellationToken ct) {
        var path = GetPath(npcId);
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<PsycheState>(stream, _options, ct);
    }

    public async Task SaveAsync(PsycheState state, CancellationToken ct) {
        await using var stream = File.Create(GetPath(state.NpcId));
        await JsonSerializer.SerializeAsync(stream, state, _options, ct);
    }

    private string GetPath(string npcId) => Path.Combine(_directory, $"{npcId}.json");
}
