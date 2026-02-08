namespace Conversation.Psyche;

public sealed class MoodState {
    public double CurrentValence { get; set; }
    public double CurrentArousal { get; set; } = 5;
    public double CurrentControl { get; set; } = 5;
    public double BaselineValence { get; set; }
    public double BaselineArousal { get; set; } = 5;
    public double BaselineControl { get; set; } = 5;
}
