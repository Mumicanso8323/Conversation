namespace Conversation.Psyche;

public sealed class PsycheState {
    public string NpcId { get; set; } = "stilla";
    public Dictionary<DesireAxis, double> DesireTrait { get; set; } = new();
    public Dictionary<DesireAxis, double> DesireDeficit { get; set; } = new();
    public Dictionary<DesireAxis, double> DesireGain { get; set; } = new();
    public LibidoState Libido { get; set; } = new();
    public MoodState Mood { get; set; } = new();
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
