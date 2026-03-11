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

        var lastUser = messagesOrTail.LastOrDefault(m => m.Role == "user")
            ?? throw new InvalidOperationException("No user message to send.");

        var text = await _engine.ChatAsync(sid, lastUser.Content);
        return (text, sid);
    }

}



