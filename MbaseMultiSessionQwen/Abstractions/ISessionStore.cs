using Mbase.Domain;

namespace Mbase.Abstractions;

public interface ISessionStore
{
    bool TryGet(string sessionId, out SessionState state);
    SessionState Create(SessionState state);              // idempotent on same id
    bool Delete(string sessionId);
    IEnumerable<SessionState> List(int take = 100, string? model = null);
    SessionState Append(string sessionId, ChatMessage msg); // throws if unknown
}
