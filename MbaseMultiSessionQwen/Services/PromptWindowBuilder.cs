using Mbase.Domain;

namespace Mbase.Services;

public static class PromptWindowBuilder
{
    public static IReadOnlyList<ChatMessage> Build(SessionState s, int maxTokens = 80_000, int reserveForOutput = 1_000)
    {
        var kept = new List<ChatMessage>();
        int budget = Math.Max(1, maxTokens - reserveForOutput);

        // keep newest first, then reverse
        for (int i = s.History.Count - 1; i >= 0; i--)
        {
            var m = s.History[i];
            int t = EstimateTokens(m);
            if (t > budget) break;
            kept.Add(m);
            budget -= t;
        }
        kept.Reverse();

        // If we dropped older messages, prepend a server summary
        if (kept.Count < s.History.Count)
        {
            var summary = Summarize(s.History.Take(s.History.Count - kept.Count));
            kept.Insert(0,
                new ChatMessage(
                    "system",
                    $"Conversation so far (server summary): {summary}",
                    DateTimeOffset.UtcNow));
        }

       

        return kept;
    }

    private static int EstimateTokens(ChatMessage m)
        => Math.Clamp(m.Content.Length / 4, 1, int.MaxValue);

    private static string Summarize(IEnumerable<ChatMessage> msgs)
    {
        var lastUser = msgs.LastOrDefault(x => x.Role == "user")?.Content ?? "";
        return $"summary: {Trunc(lastUser, 200)}";
    }

    private static string Trunc(string s, int n)
        => s.Length <= n ? s : s[..n] + "…";
}
