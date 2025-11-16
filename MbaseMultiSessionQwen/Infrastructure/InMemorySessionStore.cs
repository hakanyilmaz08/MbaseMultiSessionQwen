using System.Collections.Concurrent;
using Mbase.Abstractions;
using Mbase.Domain;

namespace Mbase.Infrastructure;

public sealed class InMemorySessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<string, SessionState> _map = new();

    public bool TryGet(string sessionId, out SessionState state) => _map.TryGetValue(sessionId, out state!);

    public SessionState Create(SessionState state)
    {
        if (!_map.TryAdd(state.SessionId, state))
            return _map[state.SessionId];
        return state;
    }

    public bool Delete(string sessionId) => _map.TryRemove(sessionId, out _);

    public IEnumerable<SessionState> List(int take = 100, string? model = null)
        => _map.Values.Where(s => model is null || s.Model == model)
                      .OrderByDescending(s => s.UpdatedAt)
                      .Take(take);

    public SessionState Append(string sessionId, ChatMessage msg)
    {
        if (!_map.TryGetValue(sessionId, out var s))
            throw new KeyNotFoundException($"Session '{sessionId}' not found.");
        s.History.Add(msg);
        s.UpdatedAt = DateTimeOffset.UtcNow;
        return s;
    }
}
