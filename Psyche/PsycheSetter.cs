namespace Conversation.Psyche;

public static class PsycheSetter {
    public static bool TrySet(PsycheState state, string param, double value, out string? error) {
        error = null;
        var segments = param.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length != 3) {
            error = "invalid segment count";
            return false;
        }

        var domain = segments[0];
        var axisOrScope = segments[1];
        var field = segments[2];

        if (domain.Equals("desire", StringComparison.OrdinalIgnoreCase)) {
            if (!Enum.TryParse<DesireAxis>(axisOrScope, true, out var axis)) {
                error = "unknown axis";
                return false;
            }

            if (!TrySetAxisField(state.DesireTrait, state.DesireDeficit, state.DesireGain, axis, field, value)) {
                error = "unknown param";
                return false;
            }

            state.UpdatedAt = DateTimeOffset.UtcNow;
            return true;
        }

        if (domain.Equals("libido", StringComparison.OrdinalIgnoreCase)) {
            if (!Enum.TryParse<LibidoAxis>(axisOrScope, true, out var axis)) {
                error = "unknown axis";
                return false;
            }

            if (!TrySetAxisField(state.Libido.Trait, state.Libido.Deficit, state.Libido.Gain, axis, field, value)) {
                error = "unknown param";
                return false;
            }

            state.UpdatedAt = DateTimeOffset.UtcNow;
            return true;
        }

        if (domain.Equals("mood", StringComparison.OrdinalIgnoreCase)) {
            if (!TrySetMood(state.Mood, axisOrScope, field, value)) {
                error = "unknown param";
                return false;
            }

            state.UpdatedAt = DateTimeOffset.UtcNow;
            return true;
        }

        error = "unknown param";
        return false;
    }

    private static bool TrySetAxisField<TAxis>(Dictionary<TAxis, double> trait, Dictionary<TAxis, double> deficit, Dictionary<TAxis, double> gain, TAxis axis, string field, double value)
        where TAxis : notnull {
        var clamped = Clamp(value, 0, 10);
        if (field.Equals("trait", StringComparison.OrdinalIgnoreCase)) {
            trait[axis] = clamped;
            return true;
        }

        if (field.Equals("deficit", StringComparison.OrdinalIgnoreCase)) {
            deficit[axis] = clamped;
            return true;
        }

        if (field.Equals("gain", StringComparison.OrdinalIgnoreCase)) {
            gain[axis] = clamped;
            return true;
        }

        return false;
    }

    private static bool TrySetMood(MoodState mood, string scope, string field, double value) {
        if (scope.Equals("current", StringComparison.OrdinalIgnoreCase)) {
            if (field.Equals("valence", StringComparison.OrdinalIgnoreCase)) {
                mood.CurrentValence = Clamp(value, -10, 10);
                return true;
            }

            if (field.Equals("arousal", StringComparison.OrdinalIgnoreCase)) {
                mood.CurrentArousal = Clamp(value, 0, 10);
                return true;
            }

            if (field.Equals("control", StringComparison.OrdinalIgnoreCase)) {
                mood.CurrentControl = Clamp(value, 0, 10);
                return true;
            }

            return false;
        }

        if (scope.Equals("baseline", StringComparison.OrdinalIgnoreCase)) {
            if (field.Equals("valence", StringComparison.OrdinalIgnoreCase)) {
                mood.BaselineValence = Clamp(value, -10, 10);
                return true;
            }

            if (field.Equals("arousal", StringComparison.OrdinalIgnoreCase)) {
                mood.BaselineArousal = Clamp(value, 0, 10);
                return true;
            }

            if (field.Equals("control", StringComparison.OrdinalIgnoreCase)) {
                mood.BaselineControl = Clamp(value, 0, 10);
                return true;
            }

            return false;
        }

        return false;
    }

    private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));
}
