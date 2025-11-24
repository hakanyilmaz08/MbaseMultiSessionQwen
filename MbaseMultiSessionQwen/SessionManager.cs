using MbaseMultiSessionQwen;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;

public class SessionManager
{
    private readonly SessionRepo _repo;
    private readonly MbaseEngine _engine;
    private readonly string _storePath;
    private readonly JsonSerializerOptions _opts;
    private readonly int _softBudget;
    private readonly string _mode; // client | server
    private readonly string _defaultModel;
    private readonly Dictionary<string, ModelProfile> _models;

    private readonly bool _compactSend;
    private readonly int _compactLastPairs;
    private readonly bool _includeBaselineSystem; // for our compact messages
    private static readonly object _convGate = new();
    private readonly ConcurrentDictionary<string, bool> _kvNeedsPrime = new();
    private readonly ConcurrentDictionary<string, Func<string>> _payoffProviders = new();

    
    public SessionManager(SessionRepo repo, MbaseEngine engine, string storePath, JsonSerializerOptions opts, int softBudget, string mode, string defaultModel, IEnumerable<ModelProfile>? knownModels = null)
    {
        _repo = repo; _engine = engine; _storePath = storePath; _opts = opts; _softBudget = softBudget; _mode = mode;
        _defaultModel = defaultModel;
        _models = (knownModels ?? Array.Empty<ModelProfile>())
            .GroupBy(m => m.Model, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToDictionary(m => m.Model, m => m, StringComparer.OrdinalIgnoreCase);

        _compactSend = (Util.DetectEnv("COMPACT_SEND", "true")).Equals("true", StringComparison.OrdinalIgnoreCase);
        _compactLastPairs = int.TryParse(Util.DetectEnv("COMPACT_LAST_PAIRS", "6"), out var lp) ? Math.Max(1, lp) : 6;
        _includeBaselineSystem = !(Util.DetectEnv("COMPACT_INCLUDE_BASELINE_SYSTEM", "true").Equals("false", StringComparison.OrdinalIgnoreCase));
    }
    public void MarkKvRenew(string sid) => _kvNeedsPrime[sid] = true;
    public void SetPayoffProvider(string sid, Func<string> provider) => _payoffProviders[sid] = provider;
    public void Ensure(string sid, bool resetIfExists = false, string? systemPrompt = null)
    {
        if (sid is null) throw new ArgumentNullException(nameof(sid));
        sid = sid.Trim();
        var changed = false;

        // Create or reset session (with system)
        if (!_repo.Sessions.TryGetValue(sid, out var list) || resetIfExists)
        {
            var systemText = string.IsNullOrWhiteSpace(systemPrompt)
                ? $"Session={sid}. You are precise, and concise."
                : systemPrompt!;
            _repo.Sessions[sid] = new List<Message> { new("system", systemText) };
            changed = true;
        }
        else if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            // Update existing system prompt if provided
            var idx = list.FindIndex(m => m.Role == "system");
            if (idx >= 0 && list[idx].Content != systemPrompt)
            {
                list[idx] = list[idx] with { Content = systemPrompt! };
                changed = true;
            }
        }

        // Ensure meta
        if (!_repo.Meta.ContainsKey(sid))
        {
            _repo.Meta[sid] = new SessionMeta(sid, Model: _defaultModel);
            changed = true;
        }
        else if (string.IsNullOrWhiteSpace(_repo.Meta[sid].Model))
        {
            _repo.Meta[sid] = _repo.Meta[sid] with { Model = _defaultModel };
            changed = true;
        }

        // Server mode: ensure ConversationIds entry
        if (string.Equals(_mode, "server", StringComparison.OrdinalIgnoreCase))
        {
            _repo.ConversationIds ??= new();
            if (!_repo.ConversationIds.ContainsKey(sid))
            {
                _repo.ConversationIds[sid] = null;
                changed = true;
            }
        }

        if (changed) Persist();
    }

    public List<string> List() => _repo.Sessions.Keys.OrderBy(k => k).ToList();

    public SessionMeta GetMeta(string sid)
    {
        Ensure(sid);
        if (!_repo.Meta.TryGetValue(sid, out var m))
        {
            m = new SessionMeta(sid);
            _repo.Meta[sid] = m;
            Persist();
        }
        if (string.IsNullOrWhiteSpace(m.Model))
        {
            m = m with { Model = _defaultModel };
            _repo.Meta[sid] = m;
            Persist();
        }

        return m;
    }

    

    public string? GetConversationId(string sid)
    {
        return _repo.ConversationIds != null
            && _repo.ConversationIds.TryGetValue(sid, out var v)
            ? v
            : null;
    }

    public void SetModel(string sid, string model)
    {
        Ensure(sid);
        _repo.Meta[sid] = _repo.Meta[sid] with { Model = ResolveModel(model) };
        Persist();
    }

    public string GetModelForSession(string sid)
    {
        Ensure(sid);
        var meta = _repo.Meta[sid];
        var model = ResolveModel(meta.Model);

        if (_models.Count > 0 && !_models.ContainsKey(model))
        {
            Console.WriteLine($"[warn] model '{model}' not found in configured list: {string.Join(", ", _models.Keys)}");
        }

        return model;
    }

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
            _repo.Sessions[newSid][0] = _repo.Sessions[newSid][0] with
            {
                Content = $"Session={newSid}. You are helpful, precise, and concise."
            };

        if (string.Equals(_mode, "server", StringComparison.OrdinalIgnoreCase)
            && _repo.ConversationIds != null
            && _repo.ConversationIds.TryGetValue(oldSid, out var cid))
        {
            _repo.ConversationIds.Remove(oldSid);
            _repo.ConversationIds[newSid] = cid;
        }

        Persist();
    }

    public void Delete(string sid)
    {
        _repo.Sessions.Remove(sid);
        _repo.Meta.Remove(sid);
        if (_repo.ConversationIds != null)
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

            // 1) Append this user turn locally (then count turns)
            session.Add(new Message("user", userText));

            // 2) KV rotation policy (count AFTER appending)
            var clearEvery = int.TryParse(Util.DetectEnv("CLEAR_KV_EVERY", "10"), out var ce) ? Math.Max(0, ce) : 6;
            var userTurns = CountUserTurns(sid);
            var renewNow = (clearEvery > 0 && userTurns > 0 && (userTurns % clearEvery) == 0);
            if (renewNow)
                RotateEngineSid(sid); // will switch to a fresh engine conversation ID

            var engineSid = GetEngineSid(sid);   // current engine session id (may have just rotated)

            // 3) Build payload for local display/compact mode
            var sidN = Norm(sid);
            List<Message> payload = string.Equals(_mode, "server", StringComparison.OrdinalIgnoreCase)
                ? new List<Message> { session[^1] }                           // only newest user
                : (_compactSend ? BuildCompactMessagesForSend(sidN) : session);

            var knownConvId = GetConversationId(sid);
            LogContext(sid, sessionCount: session.Count, knownConvId);

            // 4) Ensure engine session (idempotent) and keep prompt/params in engine
            var modelName = GetModelForSession(sid);
            var systemPrompt = session.FirstOrDefault(m => m.Role == "system")?.Content;
            _engine.CreateOrGet(
                sessionId: engineSid,
                model: modelName,
                systemPrompt: systemPrompt,
                temperature: meta.Temperature,
                topP: meta.TopP
            );

            // 5) Prepare the text we actually send to the engine
            var lastUser = payload.LastOrDefault(m => m.Role == "user")
                ?? throw new InvalidOperationException("No user message to send.");

            string sendText = lastUser.Content;

            // If this is a renew turn, merge payoff table so far (if a provider exists)
            if (renewNow)
            {
                if (_payoffProviders != null &&
                    _payoffProviders.TryGetValue(sid, out var payoffBuilder))
                {
                    var table = payoffBuilder?.Invoke();
                    if (!string.IsNullOrWhiteSpace(table))
                        sendText = $"{table}\n\n---\n{lastUser.Content}";
                }
            }

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\n→ MBASE send [sid={sid}] ({(renewNow ? "renew" : "normal")}):\n{sendText}\n");
            Console.ResetColor();

            // 6) Call engine
            string reply;
            try
            {
                var __sw = Stopwatch.StartNew();
                reply = await _engine.ChatAsync(engineSid, sendText);
                __sw.Stop();

                var messages = _repo.Sessions[sid];
                Console.WriteLine($"[DEBUG] sid={sid}, msgCount={messages.Count}, totalChars={messages.Sum(m => (m.Content ?? string.Empty).Length)}");
                Console.WriteLine(ToDebugString(messages));

                if (ShouldLogPerf())
                {
                    var usage = TryGetUsageViaEngine(_engine, engineSid)
                                ?? new UsageInfo(ApproxTokens(sendText), ApproxTokens(reply), ApproxTokens(sendText) + ApproxTokens(reply));
                    LogPerf(sid, __sw.ElapsedMilliseconds, sendText, usage);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[Engine] " + ex.GetType().Name + ": " + ex.Message);
                if (ex.InnerException is not null)
                    Console.Error.WriteLine("[Engine.Inner] " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message);
                LogContext(sid, sessionCount: session.Count, knownConvId);
                throw;
            }

            // 7) Persist assistant reply locally
            session.Add(new Message("assistant", reply));

            // In "server" mode, track the engine conversation id we used
            if (string.Equals(_mode, "server", StringComparison.OrdinalIgnoreCase))
                SetConvId(sidN, engineSid);

            Persist();
            return reply;
        }
        catch (KeyNotFoundException kex)
        {
            Console.Error.WriteLine("Meta keys: [" + string.Join(", ", _repo.Meta.Keys) + "]");
            Console.Error.WriteLine("Session keys: [" + string.Join(", ", _repo.Sessions.Keys) + "]");
            if (_repo.ConversationIds is not null)
                Console.Error.WriteLine("ConversationIds keys: [" + string.Join(", ", _repo.ConversationIds.Keys) + "]");
            Console.Error.WriteLine("[SendAsync] " + kex);
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[SendAsync] " + ex);
            throw;
        }
    }



    private void LogContext(string sid, int sessionCount, string? knownConvId)
    {
        var model = GetModelForSession(sid);
        var baseUrl = _models.TryGetValue(model, out var prof) ? prof.BaseUrl : Util.Env("LLM_BASE_URL");
        Console.Error.WriteLine(
            $"[Context] mode={_mode}, sid={sid}, model={model}, baseUrl={baseUrl}, " +
            $"convId={(knownConvId ?? "<null>")}, sessionCount={sessionCount}, topP={(_repo.Meta.TryGetValue(sid, out var m) ? m.TopP : double.NaN)}, temp={(_repo.Meta.TryGetValue(sid, out var m2) ? m2.Temperature : double.NaN)}"
        );
    }

    private string ResolveModel(string? requested)
    {
        return string.IsNullOrWhiteSpace(requested) ? _defaultModel : requested.Trim();
    }

    private static void AddIfNotDup(List<Message> dst, Message? m)
    {
        if (m is null) return;
        if (!dst.Any(x => x.Role == m.Role && x.Content == m.Content))
            dst.Add(m);
    }

    private List<Message> BuildCompactMessagesForSend(string sid)
    {
        if (!_repo.Sessions.TryGetValue(sid, out var hist) || hist.Count == 0)
            return new List<Message>(); // should not happen after Ensure

        var systems = hist.Where(m => m.Role == "system").ToList();
        var nonSys = hist.Where(m => m.Role != "system").ToList();

        var compact = new List<Message>(capacity: 2 + _compactLastPairs * 2);

        // 1) baseline system (usually the very first one)
        if (_includeBaselineSystem && systems.Count > 0)
            AddIfNotDup(compact, systems.First());

        // 2) latest "Conversation summary" system if present
        var summarySys = systems.LastOrDefault(m => m.Content.IndexOf("Conversation summary", StringComparison.OrdinalIgnoreCase) >= 0);
        AddIfNotDup(compact, summarySys);

        // 3) latest system (policy/rules you may have appended)
        var lastSys = systems.LastOrDefault();
        if (lastSys != summarySys)
            AddIfNotDup(compact, lastSys);

        // 4) last K user/assistant turns (pairs → 2*K messages)
        int take = Math.Min(nonSys.Count, _compactLastPairs * 2);
        if (take > 0)
            compact.AddRange(nonSys.Skip(nonSys.Count - take));

        return compact;
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
        var blob = new SessionRepo(_repo.Sessions, _repo.Meta, _repo.ConversationIds);
        var tmp = _storePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(blob, _opts), Encoding.UTF8);
        File.Move(tmp, _storePath, true);
    }

    // ===================== PERF LOGGING (minimal-touch helpers) =====================

    // Toggle with env: LOG_PERF=true|false (default: true)
    private static readonly bool _logPerf =
        (Environment.GetEnvironmentVariable("LOG_PERF") ?? "true")
        .Equals("true", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldLogPerf() => _logPerf;

    // Shape compatible with OpenAI-style usage fields
    private sealed record UsageInfo(int? PromptTokens, int? CompletionTokens, int? TotalTokens);

    // Try to call engine.GetLastUsage(string sid) or inspect engine.LastUsage[sid]
    private static UsageInfo? TryGetUsageViaEngine(object engine, string sid)
    {
        try
        {
            var t = engine.GetType();

            // Preferred: GetLastUsage(string sid)
            var mi = t.GetMethod("GetLastUsage", new[] { typeof(string) });
            object? result = mi?.Invoke(engine, new object?[] { sid });

            // Fallback: LastUsage dictionary or property holding a map
            if (result is null)
            {
                var pi = t.GetProperty("LastUsage");
                var lastUsageObj = pi?.GetValue(engine);
                if (lastUsageObj is System.Collections.IDictionary dict && dict.Contains(sid))
                    result = dict[sid];
            }

            if (result is null) return null;

            var rt = result.GetType();
            int? pt = GetIntProp(rt, result, "PromptTokens") ?? GetIntProp(rt, result, "prompt_tokens");
            int? ct = GetIntProp(rt, result, "CompletionTokens") ?? GetIntProp(rt, result, "completion_tokens");
            int? tt = GetIntProp(rt, result, "TotalTokens") ?? GetIntProp(rt, result, "total_tokens");
            return new UsageInfo(pt, ct, tt);
        }
        catch
        {
            return null;
        }
    }

    private static int? GetIntProp(Type rt, object obj, string name)
    {
        var p = rt.GetProperty(name);
        if (p == null) return null;
        var v = p.GetValue(obj);
        if (v is int i) return i;
        if (v is long l && l <= int.MaxValue) return (int)l;
        if (v is string s && int.TryParse(s, out var parsed)) return parsed;
        return null;
    }

    private void LogPerf(string sid, long elapsedMs, string userPayload, UsageInfo usage)
    {
        var payloadBytes = Encoding.UTF8.GetByteCount(userPayload);
        var pt = usage.PromptTokens ?? -1;
        var ct = usage.CompletionTokens ?? -1;
        var tt = usage.TotalTokens ?? (pt >= 0 && ct >= 0 ? pt + ct : -1);

        Console.Error.WriteLine(
            $"[Perf] sid={sid} elapsed_ms={elapsedMs} " +
            $"prompt_tokens={pt} completion_tokens={ct} total_tokens={tt} " +
            $"user_payload_bytes={payloadBytes}"
        );
    }

    // ==============================================================================

    // NEW helpers — put near other helpers
    // KV: count how many user turns we've sent in this sid
    private int CountUserTurns(string sid) =>
        _repo.Sessions.TryGetValue(sid, out var s) ? s.Count(m => m.Role == "user") : 0;

    // KV: get current engine session id for this sid (defaults to sid)
    private string GetEngineSid(string sid)
    {
        if (_repo.ConversationIds == null) _repo.ConversationIds = new();
        if (!_repo.ConversationIds.TryGetValue(sid, out var v) || string.IsNullOrWhiteSpace(v))
            _repo.ConversationIds[sid] = sid;
        return _repo.ConversationIds[sid]!;
    }

    // KV: rotate engine session id to force fresh KV on the server
    private void RotateEngineSid(string sid)
    {
        if (_repo.ConversationIds == null) _repo.ConversationIds = new();
        _repo.ConversationIds[sid] = $"{sid}:{DateTime.UtcNow.Ticks}";
        Console.Error.WriteLine($"[KV] rotated engine session for sid={sid} -> {_repo.ConversationIds[sid]}");
        Persist();
    }
    private static string Norm(string sid) => (sid ?? throw new ArgumentNullException(nameof(sid))).Trim();

    private void SetConvId(string sid, string engineSid)
    {
        sid = Norm(sid);
        if (string.IsNullOrWhiteSpace(engineSid))
            throw new ArgumentException("engineSid must be non-empty.", nameof(engineSid));

        lock (_convGate)
        {
            _repo.ConversationIds[sid] = engineSid; // Dictionary setter (add/replace)
        }
    }

    

private static string ToDebugString(List<Message> messages, int maxChars = 400)
{
    var sb = new StringBuilder();

    sb.AppendLine($"Total messages: {messages.Count}");

    for (int i = 0; i < messages.Count; i++)
    {
        var m = messages[i];
        var content = m.Content ?? string.Empty;
        var preview = content.Replace("\r", " ").Replace("\n", " ");

        if (preview.Length > 120)
            preview = preview.Substring(0, 120) + "...";

        sb.AppendLine($"[{i}] ({m.Role}) {preview}");
    }

    var result = sb.ToString();
    if (result.Length > maxChars)
        result = result.Substring(0, maxChars) + "...(truncated)";

    return result;
}

}
