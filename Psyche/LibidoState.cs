namespace Conversation.Psyche;

public sealed class LibidoState {
    public Dictionary<LibidoAxis, double> Trait { get; set; } = new();
    public Dictionary<LibidoAxis, double> Deficit { get; set; } = new();
    public Dictionary<LibidoAxis, double> Gain { get; set; } = new();
}
