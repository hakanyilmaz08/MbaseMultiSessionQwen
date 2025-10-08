using System.Text;
using System.Threading;
using System.Collections.Concurrent;

/// <summary>
/// Mediator that routes/forwards messages between sessions.
/// Thread-safe (single-process) via per-session semaphores.
/// </summary>
public class SessionMediator
{
    private readonly SessionManager _manager;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public SessionMediator(SessionManager manager)
    {
        _manager = manager;
    }

    /// <summary>
    /// Send a user message to a specific session and get assistant reply.
    /// </summary>
    public async Task<string> SendToSessionAsync(string sid, string userMessage)
    {
        var gate = _locks.GetOrAdd(sid, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            _manager.Ensure(sid);
            return await _manager.SendAsync(sid, userMessage);
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Forward the LAST assistant message from source session to target session
    /// as a user message, optionally with a prefix and quoting.
    /// Returns the target session's assistant reply.
    /// </summary>
    public async Task<string> ForwardLastAssistantAsync(
        string fromSid,
        string toSid,
        string? prefix = "Forwarded from {fromSid}:",
        bool quote = true)
    {
        var srcHistory = _manager.GetHistory(fromSid);
        if (srcHistory is null || srcHistory.Count == 0)
            throw new InvalidOperationException($"Source session '{fromSid}' is empty or missing.");

        // find the last assistant message in source
        string? lastAssistant = null;
        for (int i = srcHistory.Count - 1; i >= 0; i--)
        {
            if (srcHistory[i].Role == "assistant")
            {
                lastAssistant = srcHistory[i].Content;
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(lastAssistant))
            throw new InvalidOperationException($"No assistant message found in '{fromSid}' to forward.");

        // build user content for target
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            sb.AppendLine(prefix.Replace("{fromSid}", fromSid));
            sb.AppendLine();
        }
        if (quote)
        {
            sb.AppendLine("> " + lastAssistant.Replace("\n", "\n> "));
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine(lastAssistant);
            sb.AppendLine();
        }
        sb.Append("Please analyze/respond accordingly.");

        return await SendToSessionAsync(toSid, sb.ToString());
    }

    /// <summary>
    /// Forward a CUSTOM slice of source history to target session.
    /// You decide what to forward (e.g., concat last N messages).
    /// </summary>
    public async Task<string> ForwardCustomAsync(
        string fromSid,
        string toSid,
        Func<IReadOnlyList<Message>, string> builder)
    {
        var srcHistory = _manager.GetHistory(fromSid)
                        ?? throw new InvalidOperationException($"Source session '{fromSid}' not found.");
        var userPayload = builder(srcHistory);
        if (string.IsNullOrWhiteSpace(userPayload))
            throw new ArgumentException("builder produced empty payload.");

        return await SendToSessionAsync(toSid, userPayload);
    }

    /// <summary>
    /// Bridge: ask target to answer USING source content,
    /// then optionally echo the target's answer back into the source (cross-post).
    /// </summary>
    public async Task<(string targetReply, string? echoedBack)> BridgeAsync(
        string fromSid,
        string toSid,
        string instruction = "Use the forwarded content to produce a concise answer.",
        bool echoBackToSource = false,
        string echoPrefix = "Answer from {toSid}:")
    {
        // Build payload for target
        var srcHistory = _manager.GetHistory(fromSid)
                        ?? throw new InvalidOperationException($"Source session '{fromSid}' not found.");
        var lastUser = FindLastByRole(srcHistory, "user");
        var lastAssistant = FindLastByRole(srcHistory, "assistant");

        var payload = new StringBuilder();
        payload.AppendLine($"Instruction: {instruction}");
        payload.AppendLine();
        if (lastUser is not null)
        {
            payload.AppendLine("Last user message (source):");
            payload.AppendLine("> " + lastUser.Replace("\n", "\n> "));
            payload.AppendLine();
        }
        if (lastAssistant is not null)
        {
            payload.AppendLine("Last assistant message (source):");
            payload.AppendLine("> " + lastAssistant.Replace("\n", "\n> "));
            payload.AppendLine();
        }

        var targetReply = await SendToSessionAsync(toSid, payload.ToString());

        string? echoed = null;
        if (echoBackToSource)
        {
            var prefix = echoPrefix.Replace("{toSid}", toSid);
            echoed = $"{prefix}\n\n{targetReply}";
            // record it in source as assistant OR as a user note; we’ll use user so it’s actionable
            _manager.AppendMessage(fromSid, "user", echoed);
        }

        return (targetReply, echoed);
    }

    private static string? FindLastByRole(IReadOnlyList<Message> history, string role)
    {
        for (int i = history.Count - 1; i >= 0; i--)
            if (history[i].Role == role) return history[i].Content;
        return null;
    }
}
