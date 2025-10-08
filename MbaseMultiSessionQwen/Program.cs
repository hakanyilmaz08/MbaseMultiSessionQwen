using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Headers;
using MbaseMultiSessionQwen;

// MBASE-ready C# REPL
// - Supports two modes:
//   1) client (default): OpenAI-compatible; client stores full messages and sends them every turn
//   2) server: server-managed sessions via conversation_id; client sends only the new user turn
//
// Env vars:
//   LLM_BASE_URL (e.g., http://localhost:8000/v1)
//   LLM_API_KEY  (e.g., EMPTY)
//   LLM_MODEL    (e.g., Qwen/Qwen2.5-7B-Instruct)
//   MBASE_SESSION_MODE = client | server
//   MBASE_CONV_ID_STRATEGY = sid | server      (only used when server mode)
//   SESSIONS_PATH (default: sessions.json)
//   SOFT_TOKEN_BUDGET (default: 32000)
//
// Build & run:
//   dotnet new console -n QwenChatRepl
//   cd QwenChatRepl
//   dotnet add package System.Text.Json
//   (replace Program.cs with this file)
//   dotnet run

var BASE_URL = Env("LLM_BASE_URL", "http://localhost:8000/v1");
var API_KEY = Env("LLM_API_KEY", "EMPTY");
var MODEL = Env("LLM_MODEL", "Qwen2.5-7B Instruct");

var STORE = Env("SESSIONS_PATH", "sessions.json");
var SOFT_BUDGET = int.TryParse(Env("SOFT_TOKEN_BUDGET", "32000"), out var b) ? b : 32000;

var MODE = Env("MBASE_SESSION_MODE", "client").ToLowerInvariant();            // client | server
var CONV_STRAT = Env("MBASE_CONV_ID_STRATEGY", "sid").ToLowerInvariant();     // sid | server (server mode only)

const string HelpText = """
Commands:
  /help                      Show this help
  /list                      List sessions
  /switch <sid>              Switch active session (creates if missing)
  /new <sid>                 Create and switch to a new session
  /rename <old> <new>        Rename a session
  /delete <sid>              Delete a session
  /temp <value>              Set temperature for current session (e.g., /temp 0.3)
  /topp <value>              Set top_p for current session (e.g., /topp 0.9)
  /where                     Show current session + settings (+ conversation_id if any)
  /save                      Force save to disk
  /playipd                   Play iterated prisoner's dilemma
  /exit                      Quit

Anything not starting with '/' is sent to the model in the current session.
""";

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine($"Connecting to {BASE_URL} model={MODEL} mode={MODE}");

var jsonOpts = new JsonSerializerOptions
{
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
};

var http = new HttpClient { BaseAddress = new Uri(BASE_URL) };
if (!string.IsNullOrWhiteSpace(API_KEY))
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", API_KEY);

ISessionTransport transport = MODE switch
{
    "server" => new ServerManagedTransport(http, MODEL, CONV_STRAT),
    _ => new ClientManagedTransport(http, MODEL) // default
};

var repo = SessionRepo.Load(STORE, jsonOpts);
var mgr = new SessionManager(repo, transport, STORE, jsonOpts, SOFT_BUDGET, MODE);
var mediator = new SessionMediator(mgr);

string? active = repo.Sessions.Keys.OrderBy(k => k).FirstOrDefault();
if (active == null) { active = "s1"; mgr.Ensure(active); }
Console.WriteLine($"Active session: {active}");
Console.WriteLine("Type /help for commands.\n");

while (true)
{
    Console.Write("> ");
    var line = Console.ReadLine();
    if (line is null) break;
    line = line.Trim();
    if (line.Length == 0) continue;

    if (line.StartsWith("/"))
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();
        try
        {
            switch (cmd)
            {
                case "/help":
                    Console.WriteLine(HelpText);
                    break;

                case "/list":
                    var all = mgr.List();
                    Console.WriteLine(all.Count == 0 ? "(no sessions)" : string.Join(Environment.NewLine, all));
                    break;

                case "/switch":
                    if (parts.Length < 2) { Console.WriteLine("usage: /switch <sid>"); break; }
                    active = parts[1];
                    mgr.Ensure(active);
                    Console.WriteLine($"Switched to session: {active}");
                    break;

                case "/new":
                    if (parts.Length < 2) { Console.WriteLine("usage: /new <sid>"); break; }
                    active = parts[1];
                    mgr.Ensure(active, resetIfExists: false);
                    Console.WriteLine($"Created & switched to: {active}");
                    break;

                case "/rename":
                    if (parts.Length < 3) { Console.WriteLine("usage: /rename <old> <new>"); break; }
                    mgr.Rename(parts[1], parts[2]);
                    if (active == parts[1]) active = parts[2];
                    Console.WriteLine($"renamed {parts[1]} -> {parts[2]}");
                    break;

                case "/delete":
                    if (parts.Length < 2) { Console.WriteLine("usage: /delete <sid>"); break; }
                    Console.Write($"delete {parts[1]}? Type YES: ");
                    var confirm = Console.ReadLine()?.Trim();
                    if (confirm == "YES")
                    {
                        mgr.Delete(parts[1]);
                        if (active == parts[1])
                        {
                            var list = mgr.List();
                            active = list.Count > 0 ? list[0] : null;
                        }
                        Console.WriteLine("deleted.");
                    }
                    else Console.WriteLine("aborted.");
                    break;

                case "/temp":
                    if (parts.Length < 2) { Console.WriteLine("usage: /temp <float>"); break; }
                    mgr.SetTemp(active!, double.Parse(parts[1]));
                    Console.WriteLine($"temperature[{active}] = {parts[1]}");
                    break;

                case "/topp":
                    if (parts.Length < 2) { Console.WriteLine("usage: /topp <float>"); break; }
                    mgr.SetTopP(active!, double.Parse(parts[1]));
                    Console.WriteLine($"top_p[{active}] = {parts[1]}");
                    break;

                case "/where":
                    var meta = mgr.GetMeta(active!);
                    Console.WriteLine($"session={active} temp={meta.Temperature} top_p={meta.TopP} convId={mgr.GetConversationId(active!)}");
                    break;

                case "/save":
                    mgr.ForceSave();
                    Console.WriteLine("saved.");
                    break;

                case "/playipd":
                    var ipd = new IPDRunner(mgr, mediator);
                    var result = await ipd.PlayAsync("A", "B", rounds: 10, resetPrompts: false);
                    Console.WriteLine(result.Pretty());
                    // human-readable transcript:
                    File.WriteAllText("ipd_A_vs_B.txt", result.Pretty());
                    break;

                case "/exit":
                    Console.WriteLine("bye.");
                    return;

                default:
                    Console.WriteLine("unknown command. /help for list.");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"error: {ex.Message}");
        }
        continue;
    }

    // Normal message
    if (active is null) { active = "s1"; mgr.Ensure(active); }
    try
    {
        var reply = await mgr.SendAsync(active, line);
        Console.WriteLine($"[assistant] {reply}\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"error sending message: {ex.Message}");
    }
}

static string Env(string k, string def) => Environment.GetEnvironmentVariable(k) ?? def;



// ----------------- Data & Transport -----------------

public record Message([property: JsonPropertyName("role")] string Role,
               [property: JsonPropertyName("content")] string Content);

public record SessionMeta(string Sid, double Temperature = 0.7, double TopP = 0.9);

public record SessionRepo(Dictionary<string, List<Message>> Sessions,
                   Dictionary<string, SessionMeta> Meta,
                   // For server mode we also persist conversation_id per session
                   Dictionary<string, string> ConversationIds)
{
    public static SessionRepo Load(string path, JsonSerializerOptions opts)
    {
        if (!File.Exists(path))
            return new(new(), new(), new());

        using var fs = File.OpenRead(path);
        try
        {
            var loaded = JsonSerializer.Deserialize<SessionRepo>(fs, opts);
            if (loaded is not null) return loaded;
        }
        catch { /* try legacy */ }

        fs.Position = 0;
        try
        {
            var legacy = JsonSerializer.Deserialize<Dictionary<string, List<Message>>>(fs, opts);
            if (legacy is not null)
                return new(legacy, legacy.Keys.ToDictionary(k => k, k => new SessionMeta(k)), new());
        }
        catch { }
        return new(new(), new(), new());
    }
}

public interface ISessionTransport
{
    Task<(string reply, string? conversationIdFromServer)> SendAsync(
        string model,
        List<Message> messagesOrTail,
        double temperature,
        double topP,
        string sid,
        string? knownConversationId
    );
}

public class ClientManagedTransport : ISessionTransport
{
    private readonly HttpClient _http;
    private readonly string _model;
    public ClientManagedTransport(HttpClient http, string model) { _http = http; _model = model; }

    public async Task<(string, string?)> SendAsync(string model, List<Message> messages, double temperature, double topP, string sid, string? _)
    {
        var req = new
        {
            model = _model,
            messages = messages,
            temperature = temperature,
            top_p = topP
        };
        var body = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("/v1/chat/completions", body);
        resp.EnsureSuccessStatusCode();
        var raw = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        var reply = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!;
        // OpenAI-compatible responses don't standardize conversation_id; ignore.
        return (reply, null);
    }
}

// Server-managed sessions via conversation_id.
// Two strategies:
//  - sid: use the REPL's session id as conversation_id
//  - server: trust a conversation_id returned by server (if present) and persist it
class ServerManagedTransport : ISessionTransport
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _strategy; // "sid" | "server"

    public ServerManagedTransport(HttpClient http, string model, string strategy)
    {
        _http = http; _model = model; _strategy = strategy;
    }

    public async Task<(string, string?)> SendAsync(string model, List<Message> tailOnly, double temperature, double topP, string sid, string? knownConvId)
    {
        string? conversationId = _strategy == "sid" ? sid : knownConvId;

        var req = new
        {
            model = _model,
            conversation_id = conversationId,     // MBASE-specific field (common pattern)
            messages = new[] { new { role = tailOnly[^1].Role, content = tailOnly[^1].Content } }, // only last user msg
            temperature = temperature,
            top_p = topP
        };

        var body = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("/v1/chat/completions", body);
        resp.EnsureSuccessStatusCode();
        var raw = await resp.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(raw);
        var reply = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!;
        // Try to read conversation_id if server returns it (optional)
        string? serverConvId = null;
        if (doc.RootElement.TryGetProperty("conversation_id", out var cidEl) && cidEl.ValueKind == JsonValueKind.String)
            serverConvId = cidEl.GetString();

        return (reply, serverConvId);
    }
}

public class SessionManager
{
    private readonly SessionRepo _repo;
    private readonly ISessionTransport _transport;
    private readonly string _storePath;
    private readonly JsonSerializerOptions _opts;
    private readonly int _softBudget;
    private readonly string _mode; // client | server

    public SessionManager(SessionRepo repo, ISessionTransport transport, string storePath, JsonSerializerOptions opts, int softBudget, string mode)
    {
        _repo = repo; _transport = transport; _storePath = storePath; _opts = opts; _softBudget = softBudget; _mode = mode;
    }

    public void Ensure(string sid, bool resetIfExists = false)
    {
        if (!_repo.Sessions.ContainsKey(sid) || resetIfExists)
        {
            _repo.Sessions[sid] = new List<Message> { new("system", $"Session={sid}. You are precise, and concise.") };
            if (!_repo.Meta.ContainsKey(sid)) _repo.Meta[sid] = new SessionMeta(sid);
            Persist();
        }
        if (!_repo.Meta.ContainsKey(sid)) { _repo.Meta[sid] = new SessionMeta(sid); Persist(); }
        if (_mode == "server" && !_repo.ConversationIds.ContainsKey(sid)) { _repo.ConversationIds[sid] = null!; Persist(); }
    }

    public List<string> List() => _repo.Sessions.Keys.OrderBy(k => k).ToList();

    public SessionMeta GetMeta(string sid) => _repo.Meta[sid];

    public string? GetConversationId(string sid) => _repo.ConversationIds.TryGetValue(sid, out var v) ? v : null;

    public void SetTemp(string sid, double t) { Ensure(sid); _repo.Meta[sid] = _repo.Meta[sid] with { Temperature = t }; Persist(); }

    public void SetTopP(string sid, double p) { Ensure(sid); _repo.Meta[sid] = _repo.Meta[sid] with { TopP = p }; Persist(); }

    public void Rename(string oldSid, string newSid)
    {
        if (!_repo.Sessions.ContainsKey(oldSid)) throw new Exception($"no such session: {oldSid}");
        if (_repo.Sessions.ContainsKey(newSid)) throw new Exception($"target exists: {newSid}");

        _repo.Sessions[newSid] = _repo.Sessions[oldSid];
        _repo.Sessions.Remove(oldSid);

        if (_repo.Meta.TryGetValue(oldSid, out var m))
        {
            _repo.Meta.Remove(oldSid);
            _repo.Meta[newSid] = m with { Sid = newSid };
        }

        if (_repo.Sessions[newSid].Count > 0 && _repo.Sessions[newSid][0].Role == "system")
            _repo.Sessions[newSid][0] = _repo.Sessions[newSid][0] with { Content = $"Session={newSid}. You are helpful, precise, and concise." };

        if (_repo.ConversationIds.ContainsKey(oldSid))
        {
            var id = _repo.ConversationIds[oldSid];
            _repo.ConversationIds.Remove(oldSid);
            _repo.ConversationIds[newSid] = id; // carry over
        }

        Persist();
    }

    public void Delete(string sid)
    {
        _repo.Sessions.Remove(sid);
        _repo.Meta.Remove(sid);
        _repo.ConversationIds.Remove(sid);
        Persist();
    }

    public void ForceSave() => Persist();

    public async Task<string> SendAsync(string sid, string userText)
    {
        try
        {
            Ensure(sid);

            if (!_repo.Meta.TryGetValue(sid, out var meta))
                throw new InvalidOperationException($"No meta for sid '{sid}'. Available: [{string.Join(", ", _repo.Meta.Keys)}]");

            if (!_repo.Sessions.TryGetValue(sid, out var session))
                throw new InvalidOperationException($"No session for sid '{sid}'. Available: [{string.Join(", ", _repo.Sessions.Keys)}]");

            // Append user message locally
            session.Add(new Message("user", userText));

            // In client mode, we may summarize if history too long
            if (_mode == "client") await SummarizeIfNeededAsync(sid);

            // Prepare payload based on mode
            List<Message> payload = (_mode == "server")
                ? new List<Message> { session[^1] } // only newest user message
                : session;                           // full history

            var knownConvId = GetConversationId(sid);

            LogContext(sid, sessionCount: session.Count, knownConvId);

            // Call transport
            (string reply, string? serverConvId) result;
            try
            {
                result = await _transport.SendAsync(
                    model: Util.Env("LLM_MODEL", "Qwen2.5 7B Instruct"),
                    messagesOrTail: payload,
                    temperature: meta.Temperature,
                    topP: meta.TopP,
                    sid: sid,
                    knownConversationId: knownConvId
                );
            }
            catch (HttpRequestException hrex)
            {
                Console.Error.WriteLine($"[HTTP] {hrex.Message} StatusCode={(int?)hrex.StatusCode}");
                // If your transport attaches the raw response in Data, print it:
                if (hrex.Data is { Count: > 0 })
                    Console.Error.WriteLine("[HTTP] Data: " + string.Join(" | ", hrex.Data.Keys.Cast<object>().Select(k => $"{k}={hrex.Data[k]}")));
                LogContext(sid, sessionCount: session.Count, knownConvId);
                throw;
            }
            catch (Exception txEx)
            {
                Console.Error.WriteLine("[Transport] " + txEx.GetType().Name + ": " + txEx.Message);
                if (txEx.InnerException is not null)
                    Console.Error.WriteLine("[Transport.Inner] " + txEx.InnerException.GetType().Name + ": " + txEx.InnerException.Message);
                LogContext(sid, sessionCount: session.Count, knownConvId);
                throw;
            }

            var (reply, serverConvId) = result;

            // Persist assistant reply
            session.Add(new Message("assistant", reply));

            // If server provided a conversation_id, persist it
            if (_mode == "server" && serverConvId is not null)
                _repo.ConversationIds[sid] = serverConvId;

            Persist();
            return reply;
        }
        catch (KeyNotFoundException kex)
        {
            Console.Error.WriteLine("[KeyNotFound] " + kex.Message);
            Console.Error.WriteLine("Meta keys: [" + string.Join(", ", _repo.Meta.Keys) + "]");
            Console.Error.WriteLine("Session keys: [" + string.Join(", ", _repo.Sessions.Keys) + "]");
            if (_repo.ConversationIds is not null)
                Console.Error.WriteLine("ConversationIds keys: [" + string.Join(", ", _repo.ConversationIds.Keys) + "]");
            throw; // keep original stack trace
        }
        catch (JsonException jex)
        {
            Console.Error.WriteLine("[JSON] " + jex.Message);
            Console.Error.WriteLine("Tip: server may be returning an error JSON that doesn't match expected shape (e.g., no 'choices').");
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[SendAsync] " + ex);
            throw;
        }
    }

    private static string SafeEnv(string key) =>
        Environment.GetEnvironmentVariable(key) ?? "<null>";

    private void LogContext(string sid, int sessionCount, string? knownConvId)
    {
        Console.Error.WriteLine(
            $"[Context] mode={_mode}, sid={sid}, model={SafeEnv("LLM_MODEL")}, baseUrl={SafeEnv("LLM_BASE_URL")}, " +
            $"convId={(knownConvId ?? "<null>")}, sessionCount={sessionCount}, topP={(_repo.Meta.TryGetValue(sid, out var m) ? m.TopP : double.NaN)}, temp={(_repo.Meta.TryGetValue(sid, out var m2) ? m2.Temperature : double.NaN)}"
        );
    }


    private async Task SummarizeIfNeededAsync(string sid)
    {
        var msgs = _repo.Sessions[sid];
        if (CountTokens(msgs) <= _softBudget) return;

        // keep tail intact, summarize head
        int keepTail = 8;
        if (msgs.Count <= keepTail + 1) return;

        var head = msgs.Take(msgs.Count - keepTail).ToList();
        var tail = msgs.Skip(msgs.Count - keepTail).ToList();

        var summarizePrompt = """
            Summarize the prior conversation into a compact brief that preserves facts,
            decisions, constraints, and open questions. ≤ 250 words. Use bullet points.
            """.Trim();

        // Build a one-off client-managed request for summarization
        var req = new
        {
            model = Util.Env("LLM_MODEL", "Qwen2.5 7B Instruct hebe"),
            messages = head.Concat(new[] { new Message("user", summarizePrompt) }).ToList(),
            temperature = 0.2,
            top_p = 0.9
        };

        using var http = new HttpClient { BaseAddress = new Uri(Util.Env("LLM_BASE_URL", "http://localhost:8000/v1")) };
        var key = Util.Env("LLM_API_KEY", "EMPTY");
        if (!string.IsNullOrWhiteSpace(key))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

        var body = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync("/v1/chat/completions", body);
        resp.EnsureSuccessStatusCode();

        var raw = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        var summary = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!.Trim();

        var newMsgs = new List<Message>
        {
            msgs[0], // original system
            new Message("system", $"Conversation summary (compressed {DateTime.Now:yyyy-MM-dd HH:mm}):\n{summary}")
        };
        newMsgs.AddRange(tail);
        _repo.Sessions[sid] = newMsgs;
        Persist();
    }

    private static int CountTokens(IEnumerable<Message> messages)
        => messages.Sum(m => ApproxTokens(m.Content) + 4); // rough overhead

    private static int ApproxTokens(string s) => string.IsNullOrEmpty(s) ? 1 : Math.Max(1, s.Length / 4);

    private void Persist()
    {
        var blob = new SessionRepo(_repo.Sessions, _repo.Meta, _repo.ConversationIds);
        var tmp = _storePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(blob, _opts), Encoding.UTF8);
        File.Move(tmp, _storePath, true);
    }

    
    public IReadOnlyList<Message>? GetHistory(string sid)
    {
        return _repo.Sessions.TryGetValue(sid, out var list) ? list.AsReadOnly() : null;
    }

    
    public void AppendMessage(string sid, string role, string content)
    {
        Ensure(sid);
        _repo.Sessions[sid].Add(new Message(role, content));
        // do not trigger summarize here; it's cheap and safe to leave as-is
        var blob = new SessionRepo(_repo.Sessions, _repo.Meta, _repo.ConversationIds);
        var tmp = _storePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(blob, _opts), Encoding.UTF8);
        File.Move(tmp, _storePath, true);
    }

}
