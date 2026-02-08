namespace Conversation.Config;

public sealed class AssetLibrary {
    public string RootDir { get; init; } = string.Empty;
    public IReadOnlyList<string> StandeeFiles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BackgroundFiles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BgmFiles { get; init; } = Array.Empty<string>();
}

public static class AssetScanner {
    private static readonly HashSet<string> StandeeExt = new(StringComparer.OrdinalIgnoreCase) { ".png" };
    private static readonly HashSet<string> BackgroundExt = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp" };
    private static readonly HashSet<string> BgmExt = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav" };

    public static AssetLibrary Scan() {
        var root = Directory.Exists(AppPaths.DataAssetsDir) ? AppPaths.DataAssetsDir : AppPaths.BaseAssetsDir;

        var standeeDir = Path.Combine(root, "standee");
        var backgroundDir = Path.Combine(root, "background");
        var bgmDir = Path.Combine(root, "bgm");

        return new AssetLibrary {
            RootDir = root,
            StandeeFiles = ScanFiles(standeeDir, StandeeExt),
            BackgroundFiles = ScanFiles(backgroundDir, BackgroundExt),
            BgmFiles = ScanFiles(bgmDir, BgmExt),
        };
    }

    private static IReadOnlyList<string> ScanFiles(string dir, HashSet<string> allowedExt) {
        if (!Directory.Exists(dir)) {
            return Array.Empty<string>();
        }

        try {
            return Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
                .Where(path => allowedExt.Contains(Path.GetExtension(path)))
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray()!;
        }
        catch {
            return Array.Empty<string>();
        }
    }
}
