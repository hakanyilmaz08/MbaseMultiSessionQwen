public interface ISessionClient
{
    Task<string> CreateAsync(string model, string? systemPrompt = null, CancellationToken ct = default);
    Task<(string output, int promptTok, int complTok)> SendAsync(string sessionId, string input, CancellationToken ct = default);
    Task DeleteAsync(string sessionId, CancellationToken ct = default);
}