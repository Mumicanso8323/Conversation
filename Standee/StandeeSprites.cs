namespace Conversation.Standee;

public static class StandeeSprites {
    public const string Default = "普通.png";

    public static readonly HashSet<string> Allowed = new(StringComparer.Ordinal) {
        "普通.png",
        "笑顔.png",
        "期待、心躍る.png",
        "ちょっとした不満.png",
        "悲しい、困る、憐憫.png",
        "恥じらい、照れ、恍惚.png",
        "不穏な笑み、たくらみ.png"
    };

    public static string Normalize(string? sprite) {
        if (string.IsNullOrWhiteSpace(sprite)) {
            return Default;
        }

        var trimmed = sprite.Trim();
        return Allowed.Contains(trimmed) ? trimmed : Default;
    }
}
