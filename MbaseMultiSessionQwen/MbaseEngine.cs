// MbaseEngine.cs
using Mbase.Abstractions;
using Mbase.Domain;
using Mbase.Services;
using System.Collections.Concurrent;

public sealed class MbaseEngine
{
    private readonly ISessionStore _store;
    private readonly IModelBroker _broker;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public MbaseEngine(ISessionStore store, IModelBroker broker)
    { _store = store; _broker = broker; }

    public SessionState CreateOrGet(string sessionId, string model, string? systemPrompt = null,
                                    double? temperature = null, double? topP = null)
    {
        if (!_store.TryGet(sessionId, out var s))
        {
            s = new SessionState
            {
                SessionId = sessionId,
                Model = model,
                SystemPrompt = systemPrompt,
                Temperature = temperature ?? 0.7,
                TopP = topP ?? 0.9
            };
            _store.Create(s);
        }
        else
        {
            if (systemPrompt is not null) s.SystemPrompt = systemPrompt;
            if (temperature is not null) s.Temperature = temperature.Value;
            if (topP is not null) s.TopP = topP.Value;
            s.UpdatedAt = DateTimeOffset.UtcNow;
        }
        return s;
    }

    public async Task<string> ChatAsync(string sessionId, string userInput,
                                        int maxTokens = 8000, int reserveForOutput = 1000,
                                        CancellationToken ct = default)
    {
        if (!_store.TryGet(sessionId, out var s))
            throw new KeyNotFoundException($"Session '{sessionId}' not found.");

        var gate = _locks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            _store.Append(sessionId, new ChatMessage("user", userInput, DateTimeOffset.UtcNow));

            var window = PromptWindowBuilder.Build(s, maxTokens, reserveForOutput);

            var (text, usage, kv) = await _broker.CompleteAsync(
                model: s.Model,
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

            _store.Append(sessionId, new ChatMessage("assistant", text, DateTimeOffset.UtcNow));
            return text;
        }
        finally { gate.Release(); }
    }

    public bool Update(string sessionId, string? systemPrompt = null, double? temperature = null, double? topP = null)
    {
        if (!_store.TryGet(sessionId, out var s)) return false;
        if (systemPrompt is not null) s.SystemPrompt = systemPrompt;
        if (temperature is not null) s.Temperature = temperature.Value;
        if (topP is not null) s.TopP = topP.Value;
        s.UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }

    public bool Reset(string sessionId, bool keepSystemPrompt = true)
    {
        if (!_store.TryGet(sessionId, out var s)) return false;
        var sys = s.SystemPrompt;
        s.History.Clear();
        if (keepSystemPrompt) s.SystemPrompt = sys;
        s.UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }
}
