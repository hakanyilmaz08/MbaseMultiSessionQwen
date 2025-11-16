using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mbase.Abstractions;
using Mbase.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;


namespace Mbase.Brokers;

public sealed class MbaseBrokerOptions
{
    public int TimeoutSeconds { get; set; } = 60;
    public int DefaultMaxTokens { get; set; } = 1024;
    public Dictionary<string, ModelRoute> Routes { get; set; } = new();
}

public sealed class ModelRoute
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProviderKind Provider { get; set; }

    public required string BaseUrl { get; set; }      // e.g., http://localhost:8080
    public string? ApiKey { get; set; }               // if the backend needs it
    public string? KvParamName { get; set; }          // e.g., "slot_id" for llama.cpp
}

public enum ProviderKind
{
    OpenAI,        // Official OpenAI API
    OpenAICompat,  // vLLM, llama.cpp server, TextGen, etc. exposing /v1/chat/completions
    Ollama         // Ollama's /api/chat
}

public sealed class MbaseBroker : IModelBroker
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<MbaseBroker> _log;
    private readonly MbaseBrokerOptions _opt;

    public MbaseBroker(IHttpClientFactory http, IOptions<MbaseBrokerOptions> opt, ILogger<MbaseBroker> log)
    {
        _http = http;
        _opt = opt.Value;
        _log = log;
    }

    public async Task<(string text, (int PromptTokens, int CompletionTokens) usage, string? kvHandle)>
        CompleteAsync(string model, string? system, IReadOnlyList<ChatMessage> messages,
                      double temperature, double topP, string? kvHandle, CancellationToken ct = default)
    {
        if (!_opt.Routes.TryGetValue(model, out var route))
            throw new InvalidOperationException($"No route configured for model '{model}'.");

        return route.Provider switch
        {
            ProviderKind.OpenAI => await CallOpenAIAsync(route, model, system, messages, temperature, topP, kvHandle, ct),
            ProviderKind.OpenAICompat => await CallOpenAICompatAsync(route, model, system, messages, temperature, topP, kvHandle, ct),
            ProviderKind.Ollama => await CallOllamaAsync(route, model, system, messages, temperature, topP, kvHandle, ct),
            _ => throw new NotSupportedException($"Provider {route.Provider} not supported.")
        };
    }

    // ---------- Providers ----------

    private async Task<(string content, (int promptToks, int complToks) usage, string? nextKv)> CallOpenAICompatAsync(
      ModelRoute route, string model, string? system, IReadOnlyList<ChatMessage> msgs,
      double temperature, double topP, string? kvHandle, CancellationToken ct)
    {
        var client = CreateClient("OpenAICompat", route);
        var url = route.BaseUrl.TrimEnd('/') + "/v1/chat/completions";

        var chatMsgs = new List<object>();
        if (!string.IsNullOrWhiteSpace(system))
            chatMsgs.Add(new { role = "system", content = system });
        chatMsgs.AddRange(msgs.Select(m => new { role = m.Role, content = m.Content }));

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = chatMsgs,
            ["temperature"] = temperature,
            ["top_p"] = topP,
            ["stream"] = false,
            ["max_tokens"] = _opt.DefaultMaxTokens
        };
        if (!string.IsNullOrWhiteSpace(route.KvParamName) && !string.IsNullOrWhiteSpace(kvHandle))
            payload[route.KvParamName!] = kvHandle;

        var respJson = await PostJsonAsync(client, url, payload, ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(respJson);
        var root = doc.RootElement;

        // 1) Error envelopes first
        if (root.TryGetProperty("error", out var err))
        {
            var msg = err.TryGetProperty("message", out var em) ? em.GetString()
                    : err.ValueKind == JsonValueKind.String ? err.GetString()
                    : Probe(root);
            throw new InvalidOperationException($"Upstream error: {msg}");
        }
        if (root.TryGetProperty("object", out var obj) &&
            string.Equals(obj.GetString(), "error", StringComparison.OrdinalIgnoreCase))
        {
            var msg = root.TryGetProperty("message", out var em) ? em.GetString() : Probe(root);
            throw new InvalidOperationException($"Upstream error(object=error): {msg}");
        }

        // 2) Extract content robustly
        string content = ExtractContent(root);

        // 3) Usage (safe)
        int promptToks = 0, complToks = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number)
                promptToks = pt.GetInt32();
            if (usage.TryGetProperty("completion_tokens", out var ctoks) && ctoks.ValueKind == JsonValueKind.Number)
                complToks = ctoks.GetInt32();
        }

        // 4) Next KV handle (top-level or nested)
        string? nextKv = kvHandle;
        if (!string.IsNullOrWhiteSpace(route.KvParamName))
        {
            if (root.TryGetProperty(route.KvParamName!, out var kvEl) && kvEl.ValueKind == JsonValueKind.String)
                nextKv = kvEl.GetString();

            // sometimes providers tuck it under choices[0]
            else if (root.TryGetProperty("choices", out var choices) &&
                     choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var c0 = choices[0];
                if (c0.TryGetProperty(route.KvParamName!, out var kv2) && kv2.ValueKind == JsonValueKind.String)
                    nextKv = kv2.GetString();
            }
        }

        return (content, (promptToks, complToks), nextKv);

        // -------- local helpers --------

        static string ExtractContent(JsonElement root)
        {
            if (root.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0)
            {
                var c0 = choices[0];

                // OpenAI chat canonical: choices[0].message.content (string or array)
                if (c0.TryGetProperty("message", out var msg))
                {
                    if (msg.TryGetProperty("content", out var cont))
                    {
                        if (cont.ValueKind == JsonValueKind.String)
                            return cont.GetString() ?? string.Empty;

                        if (cont.ValueKind == JsonValueKind.Array)
                        {
                            // content parts: [{"type":"text","text":"..."}, ...]
                            var texts = new List<string>();
                            foreach (var part in cont.EnumerateArray())
                            {
                                if (part.ValueKind == JsonValueKind.String)
                                    texts.Add(part.GetString()!);
                                else if (part.ValueKind == JsonValueKind.Object &&
                                         part.TryGetProperty("text", out var t) &&
                                         t.ValueKind == JsonValueKind.String)
                                    texts.Add(t.GetString()!);
                            }
                            return string.Join("", texts);
                        }
                    }

                    // message with tool_calls but empty content
                    if (msg.TryGetProperty("tool_calls", out _))
                        return string.Empty;
                }

                // Some backends: choices[0].text
                if (c0.TryGetProperty("text", out var textElem) && textElem.ValueKind == JsonValueKind.String)
                    return textElem.GetString() ?? string.Empty;

                // Streaming-ish single shot: choices[0].delta.content
                if (c0.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("content", out var dcont) &&
                    dcont.ValueKind == JsonValueKind.String)
                    return dcont.GetString() ?? string.Empty;
            }

            throw new InvalidOperationException(
                "Unknown upstream schema. keys=" +
                string.Join(",", root.EnumerateObject().Select(p => p.Name)) +
                " ; payload=" + Probe(root));
        }

        static string Probe(JsonElement e, int maxLen = 600)
        {
            var s = e.GetRawText();
            return s.Length <= maxLen ? s : s.Substring(0, maxLen) + " …";
        }
    }

    private async Task<(string, (int, int), string?)> CallOpenAIAsync(
        ModelRoute route, string model, string? system, IReadOnlyList<ChatMessage> msgs,
        double temperature, double topP, string? kvHandle, CancellationToken ct)
    {
       
        
        var client = CreateClient("OpenAI", route, bearerAuth: true);
        var url = route.BaseUrl.TrimEnd('/') + "/v1/chat/completions";

        var chatMsgs = new List<object>();
        if (!string.IsNullOrWhiteSpace(system)) chatMsgs.Add(new { role = "system", content = system });
        chatMsgs.AddRange(msgs.Select(m => new { role = m.Role, content = m.Content }));

        var payload = new
        {
            model,
            messages = chatMsgs,
            temperature,
            top_p = topP,
            stream = false,
            max_tokens = _opt.DefaultMaxTokens
        };

        var respJson = await PostJsonAsync(client, url, payload, ct);
        using var doc = JsonDocument.Parse(respJson);

        var root = doc.RootElement;
        var content = root.GetProperty("choices")[0]
                          .GetProperty("message")
                          .GetProperty("content")
                          .GetString() ?? string.Empty;

        int promptToks = TryGetInt(root, "usage", "prompt_tokens");
        int complToks = TryGetInt(root, "usage", "completion_tokens");

        return (content, (promptToks, complToks), kvHandle);
    }

    private async Task<(string, (int, int), string?)> CallOllamaAsync(
        ModelRoute route, string model, string? system, IReadOnlyList<ChatMessage> msgs,
        double temperature, double topP, string? kvHandle, CancellationToken ct)
    {
        var client = CreateClient("Ollama", route);
        var url = route.BaseUrl.TrimEnd('/') + "/api/chat";

        var chatMsgs = new List<object>();
        if (!string.IsNullOrWhiteSpace(system)) chatMsgs.Add(new { role = "system", content = system });
        chatMsgs.AddRange(msgs.Select(m => new { role = m.Role, content = m.Content }));

        var payload = new
        {
            model,
            messages = chatMsgs,
            options = new { temperature, top_p = topP },
            stream = false
        };

        var respJson = await PostJsonAsync(client, url, payload, ct);
        using var doc = JsonDocument.Parse(respJson);

        // Ollama returns: { message: { role, content }, ... }
        var content = doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
        return (content, (0, 0), kvHandle);
    }

    // ---------- Helpers ----------

    private HttpClient CreateClient(string name, ModelRoute route, bool bearerAuth = false)
    {
        var client = _http.CreateClient(name);
        client.Timeout = TimeSpan.FromSeconds(Math.Max(1, _opt.TimeoutSeconds));
        if (bearerAuth && !string.IsNullOrWhiteSpace(route.ApiKey))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", route.ApiKey);
        }
        return client;
    }

    private static async Task<string> PostJsonAsync(HttpClient client, string url, object payload, CancellationToken ct)
    {
        using var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var resp = await client.PostAsync(url, body, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"[{(int)resp.StatusCode}] {resp.ReasonPhrase}: {text}");
        return text;
    }

    private static int TryGetInt(JsonElement root, string obj, string prop)
    {
        if (!root.TryGetProperty(obj, out var o)) return 0;
        if (!o.TryGetProperty(prop, out var p)) return 0;
        return p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
    }
}
