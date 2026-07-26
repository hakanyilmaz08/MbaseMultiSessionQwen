using System.Collections.Concurrent;
using SocialDilemmaLLMSimulation.Abstractions;
using SocialDilemmaLLMSimulation.Domain;

namespace SocialDilemmaLLMSimulation.Infrastructure;

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
}
