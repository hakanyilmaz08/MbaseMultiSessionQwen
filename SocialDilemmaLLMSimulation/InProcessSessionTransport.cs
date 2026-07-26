using SocialDilemmaLLMSimulation.Domain;
// InProcessSessionTransport.cs
public sealed class InProcessSessionTransport : ISessionTransport
{
    private readonly ChatSessionEngine _engine;
    private readonly string _model;

    public InProcessSessionTransport(ChatSessionEngine engine, string model)
    { _engine = engine; _model = model; }

    public async Task<(string reply, string? conversationIdFromServer)> SendAsync(
       string model,
       List<Message> messagesOrTail,
       double temperature,
       double topP,
       string sid,
       string? knownConversationId)
    {
        // Optional: keep system prompt on session
         var sys = messagesOrTail.FirstOrDefault(m => m.Role == "system")?.Content;
         _engine.CreateOrGet(sid, _model, systemPrompt: sys, temperature: temperature, topP: topP);

        //_engine.CreateOrGet(sid, _model); // idempotent

        var messages = messagesOrTail
            .Where(m => m.Role != "system")
            .Select(m => new ChatMessage(m.Role, m.Content, DateTimeOffset.UtcNow))
            .ToList();
        if (!messages.Any(m => m.Role == "user"))
            throw new InvalidOperationException("No user message to send.");

        var reply = await _engine.ChatAsync(sid, messages);
        return (reply.Text, sid);
    }

}


