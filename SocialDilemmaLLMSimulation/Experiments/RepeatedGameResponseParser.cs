namespace SocialDilemmaLLMSimulation;

public static class RepeatedGameResponseParser
{
    public static string? ParseMove(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var value = raw.Trim();
        return value is "c" or "d" ? value : null;
    }

    public static string? ParseGameChoice(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var normalized = raw.Trim();
        if (StartsWithChoice(normalized, "PD"))
            return "PD";
        if (StartsWithChoice(normalized, "SD"))
            return "SD";

        var tokens = normalized.Split(
            new[] { ' ', '\r', '\n', '\t', ':', ';', ',', '.', '-', '_' },
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in tokens)
        {
            if (string.Equals(token, "PD", StringComparison.OrdinalIgnoreCase))
                return "PD";
            if (string.Equals(token, "SD", StringComparison.OrdinalIgnoreCase))
                return "SD";
        }

        return null;
    }

    public static string ExtractExplanation(string raw)
    {
        const string marker = "EXPLANATION:";
        var index = raw.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0
            ? raw.Trim()
            : raw[(index + marker.Length)..].Trim();
    }

    private static bool StartsWithChoice(string value, string choice)
        => value.StartsWith(choice, StringComparison.OrdinalIgnoreCase)
            || value.StartsWith($"GAME: {choice}", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith($"GAME {choice}", StringComparison.OrdinalIgnoreCase);
}
