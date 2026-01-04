using Mbase.Abstractions;
using Mbase.Brokers;
using Mbase.Infrastructure;
using MbaseMultiSessionQwen;
using MbaseMultiSessionQwen.Brokers;
using System;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;


var models = ModelSettings.Load();
if (models.Count == 0)
    throw new InvalidOperationException("No models configured. Please set LLM_MODEL (and optional LLM_MODEL_A/LLM_MODEL_B) in your launch settings.");

var primaryModel = models[0];
var BASE_URL = string.IsNullOrWhiteSpace(primaryModel.BaseUrl) ? "http://localhost:8080" : primaryModel.BaseUrl;
var API_KEY = primaryModel.ApiKey;
var MODEL = primaryModel.Model;

var STORE = Util.Env("SESSIONS_PATH");
var SOFT_BUDGET = int.TryParse(Util.Env("SOFT_TOKEN_BUDGET"), out var b) ? b : 3200;
var MODE = Util.Env("MBASE_SESSION_MODE").ToLowerInvariant();            // client | server
//var CONV_STRAT = Util.Env("MBASE_CONV_ID_STRATEGY", "sid").ToLowerInvariant();     // sid | server (server mode only)

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
  /playisd                   Play snowdrift (iterated)
  /playboth                  Play both games (snowdrift and prisoner's dilemma) sequentially
  /exit                      Quit

Anything not starting with '/' is sent to the model in the current session.
""";

var p = Path.Combine(AppContext.BaseDirectory, "sessions.json");
if (File.Exists(p))
{
    File.SetAttributes(p, File.GetAttributes(p) & ~FileAttributes.ReadOnly);
    File.Delete(p);
}


Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine($"Connecting to {BASE_URL} model={MODEL} mode={MODE}");
Console.WriteLine($"Models configured: {ModelSettings.Describe(models)}");

var jsonOpts = new JsonSerializerOptions
{
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
};

var handler = new HttpClientHandler();
if ((Environment.GetEnvironmentVariable("ALLOW_INSECURE_SSL") ?? "false").ToLower() == "true")
    handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

var http = new HttpClient(handler) { BaseAddress = new Uri(BASE_URL) };


// var http = new HttpClient { BaseAddress = new Uri(BASE_URL) };
if (!string.IsNullOrWhiteSpace(API_KEY))
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", API_KEY);
var store = new InMemorySessionStore();

var bootstrap = MbaseBrokerSetup.Build(models);
using var sp = bootstrap.Provider;
var broker = bootstrap.Broker;
// broker=EchoBroker() or EchoBroker for test
var engine = new LlamaCppEngine(store, broker);



//ISessionTransport transport = MODE switch
//{
//    "server" => new ServerManagedTransport(http, MODEL, CONV_STRAT),
//    _ => new ClientManagedTransport(http, MODEL) // default
//};

var repo = SessionRepo.Load(STORE, jsonOpts);
var mgr = new SessionManager(repo, engine, STORE, jsonOpts, SOFT_BUDGET, MODE, MODEL, models);
var mediator = new SessionMediator(mgr);



string? active = repo.Sessions.Keys.OrderBy(k => k).FirstOrDefault();
if (active == null) { active = "s1"; mgr.Ensure(active); }
SyncSessionWithEngine(active);
Console.WriteLine($"Active session: {active}");
Console.WriteLine("Type /help for commands.\n");
DbInit.EnsureCreated();
void SyncSessionWithEngine(string sid)
{
    // Ensure local side exists
    mgr.Ensure(sid);

    // Pull local meta + first system prompt (if any)
    var meta = mgr.GetMeta(sid);
    string? sys = null;
    if (repo.Sessions.TryGetValue(sid, out var list))
        sys = list.FirstOrDefault(m => m.Role == "system")?.Content;

    // Create/update engine session (idempotent), storing system prompt & params
    var model = mgr.GetModelForSession(sid);
    engine.CreateOrGet(sid, model, systemPrompt: sys, temperature: meta.Temperature, topP: meta.TopP);
}
  

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
                case "/switch":
                    var sid = line.Length > cmd.Length
                    ? line.Substring(cmd.Length).Trim()
                    : string.Empty;

                    if (string.IsNullOrWhiteSpace(sid))
                    {
                        Console.WriteLine("usage: /switch <sid>");
                        break;
                    }

                    active = sid;
                    SyncSessionWithEngine(active);
                    Console.WriteLine($"Switched to session: {active}");
                    break;                   

                case "/new":
                    if (parts.Length < 2) { Console.WriteLine("usage: /new <sid>"); break; }
                    active = parts[1];
                    mgr.Ensure(active, resetIfExists: false);
                    SyncSessionWithEngine(active);          // NEW: sync with engine
                    Console.WriteLine($"Created & switched to: {active}");
                    break;

                case "/rename":
                    if (parts.Length < 3) { Console.WriteLine("usage: /rename <old> <new>"); break; }
                    mgr.Rename(parts[1], parts[2]);
                    if (active == parts[1]) active = parts[2];
                    SyncSessionWithEngine(active!);         // NEW: re-sync engine under new id
                    Console.WriteLine($"renamed {parts[1]} -> {parts[2]}");
                    break;

                case "/delete":
                    if (parts.Length < 2) { Console.WriteLine("usage: /delete <sid>"); break; }
                    Console.Write($"delete {parts[1]}? Type YES: ");
                    var confirm = Console.ReadLine()?.Trim();
                    if (confirm == "YES")
                    {
                        // (optional) wipe engine memory too:
                        engine.Reset(parts[1], keepSystemPrompt: false);   // clears history+sys on engine
                        mgr.Delete(parts[1]);
                        if (active == parts[1])
                        {
                            var list = mgr.List();
                            active = list.Count > 0 ? list[0] : null;
                        }
                        if (active != null) SyncSessionWithEngine(active); // keep current in-sync
                        Console.WriteLine("deleted.");
                    }
                    else Console.WriteLine("aborted.");
                    break;

                case "/temp":
                    if (parts.Length < 2) { Console.WriteLine("usage: /temp <float>"); break; }
                    var newT = double.Parse(parts[1]);
                    mgr.SetTemp(active!, newT);
                    engine.Update(active!, temperature: newT);            // NEW: push to engine
                    Console.WriteLine($"temperature[{active}] = {parts[1]}");
                    break;

                case "/topp":
                    if (parts.Length < 2) { Console.WriteLine("usage: /topp <float>"); break; }
                    var newP = double.Parse(parts[1]);
                    mgr.SetTopP(active!, newP);
                    engine.Update(active!, topP: newP);                   // NEW: push to engine
                    Console.WriteLine($"top_p[{active}] = {parts[1]}");
                    break;

                case "/sys":
                    if (parts.Length < 2) { Console.WriteLine("usage: /sys <system prompt>"); break; }
                    var sysText = line.Substring(cmd.Length).Trim();      // keep spaces intact
                    engine.Update(active!, systemPrompt: sysText);        // update on engine
                                                                          
                    var list2 = repo.Sessions[active!];
                    var idx = list2.FindIndex(m => m.Role == "system");
                    if (idx >= 0) list2[idx] = list2[idx] with { Content = sysText };
                    else list2.Insert(0, new Message("system", sysText));
                    repo.ConversationIds ??= new();
                    mgr.ForceSave();
                    Console.WriteLine("server session system prompt updated.");
                    break;

                case "/help":
                    Console.WriteLine(HelpText);
                    break;

                case "/list":
                    var all = mgr.List();
                    Console.WriteLine(all.Count == 0 ? "(no sessions)" : string.Join(Environment.NewLine, all));
                    break;                
                case "/where":
                    var meta = mgr.GetMeta(active!);
                    Console.WriteLine($"session={active} model={mgr.GetModelForSession(active!)} temp={meta.Temperature} top_p={meta.TopP} convId={mgr.GetConversationId(active!)}");
                    break;

                case "/save":
                    mgr.ForceSave();
                    Console.WriteLine("saved.");
                    break;

                case "/playipd":
                {
                    var ipd = new IPDRunner(mgr, mediator, models);
                    var runLabel = string.Empty;

                    for (int run_id = 1; run_id <= 1; run_id++)
                    {
                        Stopwatch sw = Stopwatch.StartNew();
                        var (allResults, actualRunLabel) = await ipd.RunV1ToV5SequentialAsync(runLabel, rounds: 50, false, true, run_id);
                        foreach (var kvp in allResults)
                        {
                            var version = kvp.Key;      // e.g. "v1"
                            var result = kvp.Value;     // GameResult

                            // Console
                            Console.WriteLine($"=== {version} ===");
                            Console.WriteLine(result.Pretty());

                            // File per scenario (simple, predictable)
                            var fileName = $"ipd_{version}_ethical_{actualRunLabel}_run{run_id}.txt";
                            File.WriteAllText(fileName, result.Pretty());
                        }
                        sw.Stop();
                        var fileNameforruns = $"ipd_ethicalv2_{actualRunLabel}_run{run_id}.txt";
                        File.WriteAllText(fileNameforruns,sw.Elapsed.TotalSeconds.ToString());
                    }
                    break;
                }
                case "/playisd":
                {
                    var isd = new ISDRunner(mgr, mediator, models);
                    var runLabel = string.Empty;
                    for (int run_id = 1; run_id <= 1; run_id++)
                    {
                        Stopwatch sw = Stopwatch.StartNew();
                        var (allResults, actualRunLabel) = await isd.RunV1ToV5SequentialAsync(runLabel, rounds: 50, false, true, run_id);
                        foreach (var kvp in allResults)
                        {
                            var version = kvp.Key;      // e.g. "v1"
                            var result = kvp.Value;     // GameResult

                            // Console
                            Console.WriteLine($"=== {version} ===");
                            Console.WriteLine(result.Pretty());

                            // File per scenario (simple, predictable)
                            var fileName = $"isd_{version}_ethicalv2_{actualRunLabel}_run{run_id}.txt";
                            File.WriteAllText(fileName, result.Pretty());
                        }
                        sw.Stop();
                        var fileNameforruns = $"isd_ethicalv2_{actualRunLabel}_run{run_id}.txt";
                        File.WriteAllText(fileNameforruns, sw.Elapsed.TotalSeconds.ToString());
                    }
                    break;
                }
                case "/playboth":
                {
                    var ipd2 = new IPDRunner(mgr, mediator, models);
                    var isd2 = new ISDRunner(mgr, mediator, models);
                    var runLabel2 = string.Empty;

                    for (int run_id = 2; run_id <= 10; run_id++)
                    {
                        Stopwatch sw = Stopwatch.StartNew();
                        var (allResults, actualRunLabel) = await ipd2.RunV1ToV5SequentialAsync(runLabel2, rounds: 50, false, true, run_id);
                        foreach (var kvp in allResults)
                        {
                            var version = kvp.Key;      // e.g. "v1"
                            var result = kvp.Value;     // GameResult

                            // Console]
                            Console.WriteLine($"=== {version} ===");
                            Console.WriteLine(result.Pretty());

                                // File per scenario (simple, predictable)
                                var fileName =
    $"ipd_{(models.Select(m => m.Model).Distinct().Skip(1).Any()
        ? "cross"
        : (string.IsNullOrWhiteSpace(models.FirstOrDefault()?.Model)
            ? "noname"
            : models.First().Model))}_{version}_run{run_id}.txt";

                                File.WriteAllText(fileName, result.Pretty());
                        }
                        sw.Stop();
                        var fileNameforruns = $"ipd_duration_{actualRunLabel}_run{run_id}.txt";
                        File.WriteAllText(fileNameforruns, sw.Elapsed.TotalSeconds.ToString());
                    }
                    for (int run_id = 2; run_id <= 10; run_id++)
                    {
                        Stopwatch sw = Stopwatch.StartNew();
                        var (allResults, actualRunLabel) = await isd2.RunV1ToV5SequentialAsync(runLabel2, rounds: 50, false, true,run_id);
                        foreach (var kvp in allResults)
                        {
                            var version = kvp.Key;      // e.g. "v1"
                            var result = kvp.Value;     // GameResult

                            // Console
                            Console.WriteLine($"=== {version} ===");
                            Console.WriteLine(result.Pretty());

                                // File per scenario (simple, predictable)
                                var fileName =
    $"isd_{(models.Select(m => m.Model).Distinct().Skip(1).Any()
        ? "cross"
        : (string.IsNullOrWhiteSpace(models.FirstOrDefault()?.Model)
            ? "noname"
            : models.First().Model))}_{version}_run{run_id}.txt";

                                File.WriteAllText(fileName, result.Pretty());
                        }
                        sw.Stop();
                        var fileNameforruns = $"isd_duration_{actualRunLabel}_run{run_id}.txt";
                        File.WriteAllText(fileNameforruns, sw.Elapsed.TotalSeconds.ToString());
                    }
                    break;
                }
                case "/resetkeep":
                    engine.Reset(active!, keepSystemPrompt: true);
                    Console.WriteLine("history cleared; system prompt kept.");
                    break;

                case "/resetall":
                    engine.Reset(active!, keepSystemPrompt: false);
                    Console.WriteLine("history + system prompt cleared.");
                    break;
                case "/generate":
                    var connectionString = "Data Source=ipd_results.db";
                    // Write files into the folder where the app is running
                    var outputFolder = AppContext.BaseDirectory;
                    DecisionExporter.ExportPrettyFromDecisions(connectionString, outputFolder);
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





// ----------------- Data & Transport -----------------

public record Message([property: JsonPropertyName("role")] string Role,
               [property: JsonPropertyName("content")] string Content);

public record SessionMeta(string Sid, double Temperature = 0.7, double TopP = 0.95, string Model = "");





//public class ClientManagedTransport : ISessionTransport
//{
//    private readonly HttpClient _http;
//    private readonly string _model;
//    public ClientManagedTransport(HttpClient http, string model) { _http = http; _model = model; _http.Timeout = TimeSpan.FromMinutes(5);}

//    public async Task<(string, string?)> SendAsync(string model, List<Message> messages, double temperature, double topP, string sid, string? _)
//    {
//        bool debugHttp = true; // turn off when not needed

//        var req = new
//        {
//            model = _model,
//            messages = messages,
//            temperature = temperature,
//            top_p = topP
//        };
//        var body = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json");
//        var bodyJson = JsonSerializer.Serialize(req, new JsonSerializerOptions { WriteIndented = true });
//        if (debugHttp)
//        {
//            Console.ForegroundColor = ConsoleColor.Yellow;
//            Console.WriteLine("\n[HTTP REQUEST to MBASE]");
//            Console.WriteLine($"POST {_http.BaseAddress}v1/chat/completions");
//            Console.WriteLine(bodyJson);
//            Console.ResetColor();
//        }

//        using var resp = await _http.PostAsync("/v1/chat/completions", body);
//        resp.EnsureSuccessStatusCode();
//        var raw = await resp.Content.ReadAsStringAsync();
//        using var doc = JsonDocument.Parse(raw);
//        var reply = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!;
//        // OpenAI-compatible responses don't standardize conversation_id; ignore.
//        return (reply, null);
//    }
//}

// Server-managed sessions via conversation_id.
// Two strategies:
//  - sid: use the REPL's session id as conversation_id
//  - server: trust a conversation_id returned by server (if present) and persist it
//class ServerManagedTransport : ISessionTransport
//{
//    private readonly HttpClient _http;
//    private readonly string _model;
//    private readonly string _strategy; // "sid" | "server"

//    public ServerManagedTransport(HttpClient http, string model, string strategy)
//    {
//        _http = http; _model = model; _strategy = strategy;
//    }

//    public async Task<(string, string?)> SendAsync(string model, List<Message> tailOnly, double temperature, double topP, string sid, string? knownConvId)
//    {
//        string? conversationId = _strategy == "sid" ? sid : knownConvId;

//        var req = new
//        {
//            model = _model,
//            conversation_id = conversationId,     // MBASE-specific field (common pattern)
//            messages = new[] { new { role = tailOnly[^1].Role, content = tailOnly[^1].Content } }, // only last user msg
//            temperature = temperature,
//            top_p = topP
//        };

//        var body = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json");
//        using var resp = await _http.PostAsync("/v1/chat/completions", body);
//        resp.EnsureSuccessStatusCode();
//        var raw = await resp.Content.ReadAsStringAsync();

//        using var doc = JsonDocument.Parse(raw);
//        var reply = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!;
//        // Try to read conversation_id if server returns it (optional)
//        string? serverConvId = null;
//        if (doc.RootElement.TryGetProperty("conversation_id", out var cidEl) && cidEl.ValueKind == JsonValueKind.String)
//            serverConvId = cidEl.GetString();

//        return (reply, serverConvId);
//    }
//}


