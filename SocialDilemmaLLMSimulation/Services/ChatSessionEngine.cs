// ChatSessionEngine.cs
using SocialDilemmaLLMSimulation.Abstractions;
using SocialDilemmaLLMSimulation.Domain;
using SocialDilemmaLLMSimulation.Services;
using System.Collections.Concurrent;

public sealed record ChatEngineReply(
    string Text,
    int PromptTokens,
    int CompletionTokens);

public sealed class ChatSessionEngine
{
    private readonly ISessionStore _store;
    private readonly IModelBroker _broker;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public ChatSessionEngine(ISessionStore store, IModelBroker broker)
    { _store = store; _broker = broker; }

    public SessionState CreateOrGet(
        string sessionId,
        string profileKey,
        string model,
        string? systemPrompt = null,
        double? temperature = null,
        double? topP = null)
    {
        if (!_store.TryGet(sessionId, out var s))
        {
            s = new SessionState
            {
                SessionId = sessionId,
                ProfileKey = profileKey,
                Model = model,
                SystemPrompt = systemPrompt,
                Temperature = temperature ?? throw new InvalidOperationException($"Temperature must be configured for session '{sessionId}'."),
                TopP = topP ?? throw new InvalidOperationException($"TopP must be configured for session '{sessionId}'.")
            };
            _store.Create(s);
        }
        else
        {
            s.ProfileKey = profileKey;
            s.Model = model;
            if (systemPrompt is not null) s.SystemPrompt = systemPrompt;
            if (temperature is not null) s.Temperature = temperature.Value;
            if (topP is not null) s.TopP = topP.Value;
            s.UpdatedAt = DateTimeOffset.UtcNow;
        }
        return s;
    }

    public async Task<ChatEngineReply> ChatAsync(
        string sessionId,
        IReadOnlyList<ChatMessage> messages,
        int maxTokens = 80000,
        int reserveForOutput = 1000,
        CancellationToken ct = default)
    {
        if (!_store.TryGet(sessionId, out var s))
            throw new KeyNotFoundException($"Session '{sessionId}' not found.");

        var gate = _locks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var window = PromptWindowBuilder.Build(messages, maxTokens, reserveForOutput);

            var (text, usage, kv) = await _broker.CompleteAsync(
                profileKey: s.ProfileKey,
                system: s.SystemPrompt,
                messages: window,
                temperature: s.Temperature,
                topP: s.TopP,
                kvHandle: s.KvCacheHandle,
                ct: ct
            );

            s.KvCacheHandle = kv ?? s.KvCacheHandle;
            s.PromptTokens += usage.PromptTokens;
            s.CompletionTokens += usage.CompletionTokens;
            s.UpdatedAt = DateTimeOffset.UtcNow;

            return new ChatEngineReply(text, usage.PromptTokens, usage.CompletionTokens);
        }
        finally { gate.Release(); }
    }

    public bool Update(
        string sessionId,
        string? profileKey = null,
        string? model = null,
        string? systemPrompt = null,
        double? temperature = null,
        double? topP = null)
    {
        if (!_store.TryGet(sessionId, out var s)) return false;
        if (profileKey is not null) s.ProfileKey = profileKey;
        if (model is not null) s.Model = model;
        if (systemPrompt is not null) s.SystemPrompt = systemPrompt;
        if (temperature is not null) s.Temperature = temperature.Value;
        if (topP is not null) s.TopP = topP.Value;
        s.UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }

    public bool Delete(string sessionId)
    {
        _locks.TryRemove(sessionId, out _);
        return _store.Delete(sessionId);
    }

    public int DeleteSessionFamily(string logicalSessionId)
    {
        var prefix = logicalSessionId + ":";
        var sessionIds = _store.List(int.MaxValue)
            .Select(s => s.SessionId)
            .Where(id => string.Equals(id, logicalSessionId, StringComparison.Ordinal)
                || id.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();

        foreach (var sessionId in sessionIds)
            Delete(sessionId);

        return sessionIds.Count;
    }
}
