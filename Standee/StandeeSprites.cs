namespace Conversation.Standee;

using System.IO;

public static class StandeeSprites {
    public static readonly string Default = "普通.png";

    public static readonly IReadOnlyList<string> Allowed = new[] {
        "普通.png",
        "笑顔.png",
        "期待、心躍る.png",
        "ちょっとした不満.png",
        "悲しい、困る、憐憫.png",
        "恥じらい、照れ、恍惚.png",
        "不穏な笑み、たくらみ.png"
    };

    public static bool IsAllowed(string fileName)
        => Allowed.Contains(fileName, StringComparer.Ordinal);

    public static string NormalizeOrDefault(string? fileName)
        => fileName is not null && IsAllowed(fileName) ? fileName : Default;

    public static IReadOnlyList<string> GetAvailableSprites(string assetsDir) {
        try {
            if (Directory.Exists(assetsDir)) {
                var scanned = Directory.EnumerateFiles(assetsDir, "*.png", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName)
                    .Where(static fileName => !string.IsNullOrWhiteSpace(fileName))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static fileName => fileName, StringComparer.Ordinal)
                    .ToArray();

                if (scanned.Length > 0) {
                    return scanned!;
                }
            }
        }
        catch {
            // ignore and fall back to existing static list
        }

        return Allowed;
    }
}
