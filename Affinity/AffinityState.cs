namespace Conversation.Affinity;

public sealed class AffinityState {
    public string NpcId { get; set; } = "default";
    public double Like { get; set; }
    public double Dislike { get; set; }
    public double Liked { get; set; }
    public double Disliked { get; set; }
    public double Love { get; set; }
    public double Hate { get; set; }
    public double Trust { get; set; }
    public double Respect { get; set; }
    public double SexualAwareness { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public AffinityState Clone() => (AffinityState)MemberwiseClone();
}
