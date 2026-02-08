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

    public async Task DeleteAsync(string npcId, CancellationToken cancellationToken) {
        // Validate the input
        if (string.IsNullOrWhiteSpace(npcId)) {
            throw new ArgumentException("NPC ID cannot be null or empty.", nameof(npcId));
        }

        // Construct the file path for the NPC
        string filePath = GetPath(npcId);

        // Check if the file exists
        if (File.Exists(filePath)) {
            // Delete the file asynchronously
            await Task.Run(() => File.Delete(filePath), cancellationToken);
        } else {
            throw new FileNotFoundException("NPC file not found.", filePath);
        }
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
