namespace Conversation.Standee;

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
}
