
using System.Net.Http.Json;
using System.Text.Json;

public sealed class HttpSessionClient : ISessionClient
{
    private readonly HttpClient _http; private readonly string _base;
    public HttpSessionClient(HttpClient http, string baseUrl) { _http = http; _base = baseUrl.TrimEnd('/'); }

    public async Task<string> CreateAsync(string model, string? systemPrompt = null, CancellationToken ct = default)
    {
        var res = await _http.PostAsJsonAsync($"{_base}/v1/sessions", new { model, systemPrompt }, ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        return json.GetProperty("session_id").GetString()!;
    }

    public async Task<(string, int, int)> SendAsync(string sessionId, string input, CancellationToken ct = default)
    {
        var res = await _http.PostAsJsonAsync($"{_base}/v1/chat", new { sessionId, input }, ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        var outp = json.GetProperty("output").GetString()!;
        int pt = json.TryGetProperty("usage", out var u) && u.TryGetProperty("prompt", out var p) ? p.GetInt32() : 0;
        int ctok = u.ValueKind != JsonValueKind.Undefined && u.TryGetProperty("completion", out var c) ? c.GetInt32() : 0;
        return (outp, pt, ctok);
    }

    public async Task DeleteAsync(string sessionId, CancellationToken ct = default)
    {
        var res = await _http.DeleteAsync($"{_base}/v1/sessions/{sessionId}", ct);
        res.EnsureSuccessStatusCode();
    }
}
