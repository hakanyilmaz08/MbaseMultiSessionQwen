using SocialDilemmaLLMSimulation;
using SocialDilemmaLLMSimulation.Domain;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;

public class SessionManager
{
    private readonly SessionRepo _repo;
    private readonly ChatSessionEngine _engine;
    private readonly string _storePath;
    private readonly JsonSerializerOptions _opts;
    private readonly string _mode; // client | server
    private readonly string _defaultModel;
    private readonly Dictionary<string, ModelProfile> _models;

    private readonly bool _compactSend;
    private readonly int _compactLastPairs;
    private readonly bool _includeBaselineSystem;
    private static readonly object _convGate = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sendLocks = new();

    public sealed record TimedReply(string Reply, TimeSpan Elapsed);

    public SessionManager(
        SessionRepo repo,
        ChatSessionEngine engine,
        string storePath,
        JsonSerializerOptions opts,
        string mode,
        string defaultModel,
        IEnumerable<ModelProfile>? knownModels = null)
    {
        _repo = repo;
        _engine = engine;
        _storePath = storePath;
        _opts = opts;
        _mode = mode;
        _defaultModel = defaultModel;
        _models = (knownModels ?? Array.Empty<ModelProfile>())
            .GroupBy(m => m.Model, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToDictionary(m => m.Model, m => m, StringComparer.OrdinalIgnoreCase);

        _compactSend = Util.DetectEnv("COMPACT_SEND", "true").Equals("true", StringComparison.OrdinalIgnoreCase);
        _compactLastPairs = int.TryParse(Util.DetectEnv("COMPACT_LAST_PAIRS", "6"), out var lp) ? Math.Max(1, lp) : 6;
        _includeBaselineSystem = !Util.DetectEnv("COMPACT_INCLUDE_BASELINE_SYSTEM", "true").Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    public void Ensure(string sid, bool resetIfExists = false, string? systemPrompt = null)
    {
        if (sid is null) throw new ArgumentNullException(nameof(sid));

        sid = sid.Trim();
        var changed = false;

        if (!_repo.Sessions.TryGetValue(sid, out var list) || resetIfExists)
        {
            var systemText = string.IsNullOrWhiteSpace(systemPrompt)
                ? $"Session={sid}. You are precise, and concise."
                : systemPrompt;
            _repo.Sessions[sid] = new List<Message> { new("system", systemText!) };
            changed = true;
        }
        else if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            var idx = list.FindIndex(m => m.Role == "system");
            if (idx >= 0 && list[idx].Content != systemPrompt)
            {
                list[idx] = list[idx] with { Content = systemPrompt };
                changed = true;
            }
        }

        var currentMeta = _repo.Meta.TryGetValue(sid, out var existingMeta)
            ? existingMeta
            : CreateConfiguredMeta(sid, _defaultModel);
        var updatedMeta = BuildConfiguredMeta(currentMeta with { Sid = sid });
        if (!EqualityComparer<SessionMeta>.Default.Equals(currentMeta, updatedMeta))
        {
            _repo.Meta[sid] = updatedMeta;
            changed = true;
        }
        else if (!_repo.Meta.ContainsKey(sid))
        {
            _repo.Meta[sid] = updatedMeta;
            changed = true;
        }

        if (string.Equals(_mode, "server", StringComparison.OrdinalIgnoreCase))
        {
            _repo.ConversationIds ??= new();
            if (!_repo.ConversationIds.ContainsKey(sid))
            {
                _repo.ConversationIds[sid] = null;
                changed = true;
            }
        }

        if (changed)
            Persist();
    }

    public List<string> List() => _repo.Sessions.Keys.OrderBy(k => k).ToList();

    public SessionMeta GetMeta(string sid)
    {
        Ensure(sid);
        return _repo.Meta[sid];
    }

    public string? GetConversationId(string sid)
    {
        return _repo.ConversationIds != null
            && _repo.ConversationIds.TryGetValue(sid, out var value)
            ? value
            : null;
    }

    public void SetModel(string sid, string model)
    {
        Ensure(sid);

        var currentMeta = _repo.Meta[sid];
        var updatedMeta = BuildConfiguredMeta(currentMeta with { Model = model });
        if (EqualityComparer<SessionMeta>.Default.Equals(currentMeta, updatedMeta))
            return;

        _repo.Meta[sid] = updatedMeta;
        Persist();
    }

    public string GetModelForSession(string sid)
    {
        Ensure(sid);
        return _repo.Meta[sid].Model;
    }

    public void SetTemp(string sid, double temperature)
    {
        if (temperature < 0.0 || temperature > 2.0)
            throw new ArgumentOutOfRangeException(nameof(temperature), "Temperature must be between 0.0 and 2.0.");

        Ensure(sid);
        _repo.Meta[sid] = _repo.Meta[sid] with { Temperature = temperature, TemperatureOverridden = true };
        Persist();
    }

    public void SetTopP(string sid, double topP)
    {
        if (topP < 0.0 || topP > 1.0)
            throw new ArgumentOutOfRangeException(nameof(topP), "TopP must be between 0.0 and 1.0.");

        Ensure(sid);
        _repo.Meta[sid] = _repo.Meta[sid] with { TopP = topP, TopPOverridden = true };
        Persist();
    }

    public void Rename(string oldSid, string newSid)
    {
        if (!_repo.Sessions.ContainsKey(oldSid)) throw new Exception($"no such session: {oldSid}");
        if (_repo.Sessions.ContainsKey(newSid)) throw new Exception($"target exists: {newSid}");

        _repo.Sessions[newSid] = _repo.Sessions[oldSid];
        _repo.Sessions.Remove(oldSid);

        if (_repo.Meta.TryGetValue(oldSid, out var meta))
        {
            _repo.Meta.Remove(oldSid);
            _repo.Meta[newSid] = meta with { Sid = newSid };
        }

        if (_repo.Sessions[newSid].Count > 0 && _repo.Sessions[newSid][0].Role == "system")
        {
            _repo.Sessions[newSid][0] = _repo.Sessions[newSid][0] with
            {
                Content = $"Session={newSid}. You are helpful, precise, and concise."
            };
        }

        if (string.Equals(_mode, "server", StringComparison.OrdinalIgnoreCase)
            && _repo.ConversationIds != null
            && _repo.ConversationIds.TryGetValue(oldSid, out var conversationId))
        {
            _repo.ConversationIds.Remove(oldSid);
            _repo.ConversationIds[newSid] = conversationId;
        }

        Persist();
    }

    public void Delete(string sid)
    {
        _repo.Sessions.Remove(sid);
        _repo.Meta.Remove(sid);
        _repo.ConversationIds?.Remove(sid);
        Persist();
    }

    public void ForceSave() => Persist();

    public Task<TimedReply> SendTimedAsync(string sid, string userText, Func<string>? kvRenewalContextProvider = null)
        => WithSessionLockAsync(sid, async () =>
        {
            Ensure(sid);
            var stopwatch = Stopwatch.StartNew();
            var reply = await SendCoreAsync(sid, userText, kvRenewalContextProvider);
            stopwatch.Stop();
            return new TimedReply(reply, stopwatch.Elapsed);
        });

    public Task<string> SendAsync(string sid, string userText, Func<string>? kvRenewalContextProvider = null)
        => WithSessionLockAsync(sid, async () =>
        {
            Ensure(sid);
            return await SendCoreAsync(sid, userText, kvRenewalContextProvider);
        });

    private async Task<T> WithSessionLockAsync<T>(string sid, Func<Task<T>> action)
    {
        var gate = _sendLocks.GetOrAdd(sid, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            return await action();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string> SendCoreAsync(string sid, string userText, Func<string>? kvRenewalContextProvider)
    {
        try
        {
            if (!_repo.Meta.TryGetValue(sid, out var meta))
                throw new InvalidOperationException($"No meta for sid '{sid}'. Available: [{string.Join(", ", _repo.Meta.Keys)}]");

            if (!_repo.Sessions.TryGetValue(sid, out var session))
                throw new InvalidOperationException($"No session for sid '{sid}'. Available: [{string.Join(", ", _repo.Sessions.Keys)}]");

            session.Add(new Message("user", userText));

            var clearEveryDefault = _compactSend ? "10" : "0";
            var clearEvery = int.TryParse(Util.DetectEnv("CLEAR_KV_EVERY", clearEveryDefault), out var configuredValue)
                ? Math.Max(0, configuredValue)
                : 6;
            var userTurns = CountUserTurns(sid);
            var renewNow = clearEvery > 0 && userTurns > 0 && (userTurns % clearEvery) == 0;
            if (renewNow)
                RotateEngineSid(sid);

            var engineSid = GetEngineSid(sid);
            var normalizedSid = Norm(sid);
            List<Message> payload = string.Equals(_mode, "server", StringComparison.OrdinalIgnoreCase)
                ? new List<Message> { session[^1] }
                : (_compactSend ? BuildCompactMessagesForSend(normalizedSid) : session);

            var knownConvId = GetConversationId(sid);
            LogContext(sid, session.Count, knownConvId);

            var modelName = GetModelForSession(sid);
            var systemPrompt = session.FirstOrDefault(m => m.Role == "system")?.Content;
            _engine.CreateOrGet(
                sessionId: engineSid,
                model: modelName,
                systemPrompt: systemPrompt,
                temperature: meta.Temperature,
                topP: meta.TopP);

            var lastUser = payload.LastOrDefault(m => m.Role == "user")
                ?? throw new InvalidOperationException("No user message to send.");

            var sendText = lastUser.Content;
            if (renewNow && kvRenewalContextProvider is not null)
            {
                var context = kvRenewalContextProvider();
                if (!string.IsNullOrWhiteSpace(context))
                    sendText = $"{context}\n\n---\n{lastUser.Content}";
            }

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\n-> MBASE send [sid={sid}] ({(renewNow ? "renew" : "normal")}):\n{sendText}\n");
            Console.ResetColor();

            string reply;
            try
            {
                var stopwatch = Stopwatch.StartNew();
                reply = await _engine.ChatAsync(engineSid, sendText);
                stopwatch.Stop();

                var messages = _repo.Sessions[sid];
                Console.WriteLine($"[DEBUG] sid={sid}, msgCount={messages.Count}, totalChars={messages.Sum(m => (m.Content ?? string.Empty).Length)}");
                Console.WriteLine(ToDebugString(messages));

                if (ShouldLogPerf())
                {
                    var usage = TryGetUsageViaEngine(_engine, engineSid)
                                ?? new UsageInfo(ApproxTokens(sendText), ApproxTokens(reply), ApproxTokens(sendText) + ApproxTokens(reply));
                    LogPerf(sid, stopwatch.ElapsedMilliseconds, sendText, usage);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[Engine] " + ex.GetType().Name + ": " + ex.Message);
                if (ex.InnerException is not null)
                    Console.Error.WriteLine("[Engine.Inner] " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message);
                LogContext(sid, session.Count, knownConvId);
                throw;
            }

            session.Add(new Message("assistant", reply));

            if (string.Equals(_mode, "server", StringComparison.OrdinalIgnoreCase))
                SetConvId(normalizedSid, engineSid);

            Persist();
            return reply;
        }
        catch (KeyNotFoundException keyNotFound)
        {
            Console.Error.WriteLine("Meta keys: [" + string.Join(", ", _repo.Meta.Keys) + "]");
            Console.Error.WriteLine("Session keys: [" + string.Join(", ", _repo.Sessions.Keys) + "]");
            if (_repo.ConversationIds is not null)
                Console.Error.WriteLine("ConversationIds keys: [" + string.Join(", ", _repo.ConversationIds.Keys) + "]");
            Console.Error.WriteLine("[SendAsync] " + keyNotFound);
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[SendAsync] " + ex);
            throw;
        }
    }

    private SessionMeta BuildConfiguredMeta(SessionMeta meta)
    {
        var model = ResolveConfiguredModel(meta.Model);
        var profile = ResolveConfiguredProfile(model);

        return meta with
        {
            Model = model,
            Temperature = meta.TemperatureOverridden ? meta.Temperature : profile.Temperature,
            TopP = meta.TopPOverridden ? meta.TopP : profile.TopP
        };
    }

    private SessionMeta CreateConfiguredMeta(string sid, string requestedModel)
    {
        var profile = ResolveConfiguredProfile(requestedModel);
        return new SessionMeta(sid, profile.Temperature, profile.TopP, profile.Model, false, false);
    }

    private ModelProfile ResolveConfiguredProfile(string? requestedModel)
    {
        var model = ResolveModel(requestedModel);
        if (_models.TryGetValue(model, out var profile))
            return profile;

        if (_models.TryGetValue(_defaultModel, out var fallback))
            return fallback;

        throw new InvalidOperationException($"No configured model profile was found for '{model}', and no fallback profile exists.");
    }

    private string ResolveConfiguredModel(string? requested)
    {
        var model = ResolveModel(requested);

        if (_models.Count > 0 && !_models.ContainsKey(model))
        {
            Console.WriteLine($"[warn] model '{model}' not found in configured list: {string.Join(", ", _models.Keys)}. Falling back to '{_defaultModel}'.");
            return _defaultModel;
        }

        return model;
    }

    private void LogContext(string sid, int sessionCount, string? knownConvId)
    {
        var model = GetModelForSession(sid);
        var baseUrl = _models.TryGetValue(model, out var profile) ? profile.BaseUrl : Util.Env("LLM_BASE_URL");
        Console.Error.WriteLine(
            $"[Context] mode={_mode}, sid={sid}, model={model}, baseUrl={baseUrl}, " +
            $"convId={(knownConvId ?? "<null>")}, sessionCount={sessionCount}, topP={(_repo.Meta.TryGetValue(sid, out var topPMeta) ? topPMeta.TopP : double.NaN)}, temp={(_repo.Meta.TryGetValue(sid, out var tempMeta) ? tempMeta.Temperature : double.NaN)}");
    }

    private string ResolveModel(string? requested)
        => string.IsNullOrWhiteSpace(requested) ? _defaultModel : requested.Trim();

    private static void AddIfNotDup(List<Message> destination, Message? message)
    {
        if (message is null)
            return;

        if (!destination.Any(existing => existing.Role == message.Role && existing.Content == message.Content))
            destination.Add(message);
    }

    private List<Message> BuildCompactMessagesForSend(string sid)
    {
        if (!_repo.Sessions.TryGetValue(sid, out var history) || history.Count == 0)
            return new List<Message>();

        var systems = history.Where(m => m.Role == "system").ToList();
        var nonSystem = history.Where(m => m.Role != "system").ToList();
        var compact = new List<Message>(capacity: 2 + _compactLastPairs * 2);

        if (_includeBaselineSystem && systems.Count > 0)
            AddIfNotDup(compact, systems.First());

        var summarySystem = systems.LastOrDefault(m => m.Content.IndexOf("Conversation summary", StringComparison.OrdinalIgnoreCase) >= 0);
        AddIfNotDup(compact, summarySystem);

        var lastSystem = systems.LastOrDefault();
        if (lastSystem != summarySystem)
            AddIfNotDup(compact, lastSystem);

        var take = Math.Min(nonSystem.Count, _compactLastPairs * 2);
        if (take > 0)
            compact.AddRange(nonSystem.Skip(nonSystem.Count - take));

        return compact;
    }

    private static int ApproxTokens(string text)
        => string.IsNullOrEmpty(text) ? 1 : Math.Max(1, text.Length / 4);

    private void Persist()
    {
        var blob = new SessionRepo(_repo.Sessions, _repo.Meta, _repo.ConversationIds);
        var tmp = _storePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(blob, _opts), Encoding.UTF8);
        File.Move(tmp, _storePath, true);
    }

    public IReadOnlyList<Message>? GetHistory(string sid)
        => _repo.Sessions.TryGetValue(sid, out var list) ? list.AsReadOnly() : null;

    public void AppendMessage(string sid, string role, string content)
    {
        Ensure(sid);
        _repo.Sessions[sid].Add(new Message(role, content));
        Persist();
    }

    private static readonly bool _logPerf =
        (Environment.GetEnvironmentVariable("LOG_PERF") ?? "true")
        .Equals("true", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldLogPerf() => _logPerf;

    private sealed record UsageInfo(int? PromptTokens, int? CompletionTokens, int? TotalTokens);

    private static UsageInfo? TryGetUsageViaEngine(object engine, string sid)
    {
        try
        {
            var type = engine.GetType();
            var method = type.GetMethod("GetLastUsage", new[] { typeof(string) });
            object? result = method?.Invoke(engine, new object?[] { sid });

            if (result is null)
            {
                var property = type.GetProperty("LastUsage");
                var lastUsage = property?.GetValue(engine);
                if (lastUsage is System.Collections.IDictionary dictionary && dictionary.Contains(sid))
                    result = dictionary[sid];
            }

            if (result is null)
                return null;

            var resultType = result.GetType();
            var promptTokens = GetIntProp(resultType, result, "PromptTokens") ?? GetIntProp(resultType, result, "prompt_tokens");
            var completionTokens = GetIntProp(resultType, result, "CompletionTokens") ?? GetIntProp(resultType, result, "completion_tokens");
            var totalTokens = GetIntProp(resultType, result, "TotalTokens") ?? GetIntProp(resultType, result, "total_tokens");
            return new UsageInfo(promptTokens, completionTokens, totalTokens);
        }
        catch
        {
            return null;
        }
    }

    private static int? GetIntProp(Type resultType, object instance, string name)
    {
        var property = resultType.GetProperty(name);
        if (property == null)
            return null;

        var value = property.GetValue(instance);
        if (value is int intValue)
            return intValue;
        if (value is long longValue && longValue <= int.MaxValue)
            return (int)longValue;
        if (value is string stringValue && int.TryParse(stringValue, out var parsed))
            return parsed;
        return null;
    }

    private void LogPerf(string sid, long elapsedMs, string userPayload, UsageInfo usage)
    {
        var payloadBytes = Encoding.UTF8.GetByteCount(userPayload);
        var promptTokens = usage.PromptTokens ?? -1;
        var completionTokens = usage.CompletionTokens ?? -1;
        var totalTokens = usage.TotalTokens ?? (promptTokens >= 0 && completionTokens >= 0 ? promptTokens + completionTokens : -1);

        Console.Error.WriteLine(
            $"[Perf] sid={sid} elapsed_ms={elapsedMs} " +
            $"prompt_tokens={promptTokens} completion_tokens={completionTokens} total_tokens={totalTokens} " +
            $"user_payload_bytes={payloadBytes}");
    }

    private int CountUserTurns(string sid)
        => _repo.Sessions.TryGetValue(sid, out var session) ? session.Count(m => m.Role == "user") : 0;

    private string GetEngineSid(string sid)
    {
        _repo.ConversationIds ??= new();
        if (!_repo.ConversationIds.TryGetValue(sid, out var value) || string.IsNullOrWhiteSpace(value))
            _repo.ConversationIds[sid] = sid;
        return _repo.ConversationIds[sid]!;
    }

    private void RotateEngineSid(string sid)
    {
        _repo.ConversationIds ??= new();
        _repo.ConversationIds[sid] = $"{sid}:{DateTime.UtcNow.Ticks}";
        Console.Error.WriteLine($"[KV] rotated engine session for sid={sid} -> {_repo.ConversationIds[sid]}");
        Persist();
    }

    private static string Norm(string sid)
        => (sid ?? throw new ArgumentNullException(nameof(sid))).Trim();

    private void SetConvId(string sid, string engineSid)
    {
        sid = Norm(sid);
        if (string.IsNullOrWhiteSpace(engineSid))
            throw new ArgumentException("engineSid must be non-empty.", nameof(engineSid));

        lock (_convGate)
        {
            _repo.ConversationIds![sid] = engineSid;
        }
    }

    private static string ToDebugString(List<Message> messages, int maxChars = 400)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Total messages: {messages.Count}");

        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            var preview = (message.Content ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
            if (preview.Length > 120)
                preview = preview[..120] + "...";

            builder.AppendLine($"[{i}] ({message.Role}) {preview}");
        }

        var result = builder.ToString();
        if (result.Length > maxChars)
            result = result[..maxChars] + "...(truncated)";

        return result;
    }
}



