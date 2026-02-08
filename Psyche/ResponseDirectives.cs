using System.Text.Json;

namespace Conversation.Psyche;

public sealed class ResponseDirectives {
    public List<string> PriorityGoals { get; set; } = new();
    public List<string> Avoid { get; set; } = new();
    public DirectiveBounds Bounds { get; set; } = new();
    public List<string> Notes { get; set; } = new();

    public static ResponseDirectives SafeDefault() => new() {
        Bounds = new DirectiveBounds {
            PersonaImmutable = true,
            NoStyleChange = true,
            SexualSafetyStrict = true
        }
    };

    public string ToSystemBlock() {
        var normalized = this;
        normalized.Bounds.PersonaImmutable = true;
        normalized.Bounds.NoStyleChange = true;
        normalized.Bounds.SexualSafetyStrict = true;

        var json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        return """
[DIRECTIVES]
DIRECTIVES は会話の内容優先度のみを扱う。文体/一人称/二人称/キャラ性は Persona を厳守し、変更しないこと。
""" + "\n" + json;
    }
}

public sealed class DirectiveBounds {
    public bool PersonaImmutable { get; set; } = true;
    public bool NoStyleChange { get; set; } = true;
    public bool SexualSafetyStrict { get; set; } = true;
}
