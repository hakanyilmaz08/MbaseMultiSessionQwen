using SocialDilemmaLLMSimulation.Infrastructure;
using SocialDilemmaLLMSimulation.Brokers;
using SocialDilemmaLLMSimulation.Domain;
using System.Text.Json;

namespace SocialDilemmaLLMSimulation;

public class SessionCoordinator : IDisposable
{
    private readonly SessionRepo _repo;
    private readonly string _storePath;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly string _mode;
    private readonly StartupModelSelection _launchSelection;

    private IDisposable? _brokerProvider;
    private ChatSessionEngine _engine = null!;

    public SessionCoordinator(string storePath, JsonSerializerOptions jsonOptions, string mode)
    {
        _storePath = storePath;
        _jsonOptions = jsonOptions;
        _mode = mode;
        _repo = SessionRepo.Load(storePath, jsonOptions);
        _launchSelection = ModelSettings.CreateLaunchSelection();
        CurrentSelection = _launchSelection;
        Models = Array.Empty<ModelProfile>();
        Manager = null!;
    }

    public StartupModelSelection CurrentSelection { get; private set; }
    public IReadOnlyList<ModelProfile> Models { get; private set; }
    public SessionManager Manager { get; private set; }
    public string? ActiveSession { get; private set; }

    public bool Initialize()
    {
        var startupSelection = ModelSettings.ResolveStartupSelection();
        ApplyModelSelection(startupSelection, "Startup selection");

        ActiveSession = ResolveStartupSessionId(startupSelection);
        var createdFreshStartupSession = !_repo.Sessions.ContainsKey(ActiveSession);
        Manager.Ensure(ActiveSession, resetIfExists: false);
        SyncSessionWithEngine(ActiveSession);
        return createdFreshStartupSession;
    }

    public string CreateFreshSessionForSelection(StartupModelSelection selection)
        => CreateFreshSessionIdForSelection(selection);

    public IReadOnlyList<string> ListSessions() => Manager.List();

    public void SwitchSession(string sid)
    {
        ActiveSession = sid;
        SyncSessionWithEngine(sid);
    }

    public void CreateSession(string sid)
    {
        ActiveSession = sid;
        Manager.Ensure(sid, resetIfExists: false);
        SyncSessionWithEngine(sid);
    }

    public void RenameSession(string oldSid, string newSid)
    {
        Manager.Rename(oldSid, newSid);
        if (ActiveSession == oldSid)
            ActiveSession = newSid;

        if (!string.IsNullOrWhiteSpace(ActiveSession))
            SyncSessionWithEngine(ActiveSession);
    }

    public void DeleteSession(string sid)
    {
        _engine.Reset(sid, keepSystemPrompt: false);
        Manager.Delete(sid);

        if (ActiveSession == sid)
        {
            var sessions = Manager.List();
            ActiveSession = sessions.Count > 0 ? sessions[0] : null;
        }

        if (!string.IsNullOrWhiteSpace(ActiveSession))
            SyncSessionWithEngine(ActiveSession);
    }

    public void SetTemperature(double temperature)
    {
        var sid = RequireActiveSession();
        Manager.SetTemp(sid, temperature);
        _engine.Update(sid, temperature: temperature);
    }

    public void SetTopP(double topP)
    {
        var sid = RequireActiveSession();
        Manager.SetTopP(sid, topP);
        _engine.Update(sid, topP: topP);
    }

    public void SetSystemPrompt(string systemPrompt)
    {
        var sid = RequireActiveSession();
        _engine.Update(sid, systemPrompt: systemPrompt);

        var list = _repo.Sessions[sid];
        var idx = list.FindIndex(m => m.Role == "system");
        if (idx >= 0)
            list[idx] = list[idx] with { Content = systemPrompt };
        else
            list.Insert(0, new Message("system", systemPrompt));

        Manager.ForceSave();
    }

    public StartupModelSelection? PromptForConfigurationSwitch()
    {
        var selected = ModelSettings.PromptForConfigurationSelection(
            includeLaunchSelection: true,
            launchSelection: _launchSelection,
            allowCancel: true);

        if (selected is null)
            return null;

        var previousActive = ActiveSession;
        ApplyModelSelection(selected, "Configuration switched", syncActiveSession: false);

        ActiveSession = CreateFreshSessionIdForSelection(selected);
        Manager.Ensure(ActiveSession, resetIfExists: false);
        SyncSessionWithEngine(ActiveSession);

        if (!string.IsNullOrWhiteSpace(previousActive))
            Console.WriteLine($"Previous session preserved: {previousActive}");

        return selected;
    }

    public string DescribeCurrentSession()
    {
        var sid = RequireActiveSession();
        var meta = Manager.GetMeta(sid);
        return $"session={sid} cfg={CurrentSelection.Name} source={CurrentSelection.Source} model={Manager.GetModelForSession(sid)} temp={meta.Temperature} top_p={meta.TopP} convId={Manager.GetConversationId(sid)}";
    }

    public void Save() => Manager.ForceSave();

    public void ResetActiveSession(bool keepSystemPrompt)
    {
        var sid = RequireActiveSession();
        _engine.Reset(sid, keepSystemPrompt);
    }

    public string EnsureDefaultActiveSession()
    {
        if (!string.IsNullOrWhiteSpace(ActiveSession))
            return ActiveSession;

        ActiveSession = "s1";
        Manager.Ensure(ActiveSession);
        SyncSessionWithEngine(ActiveSession);
        return ActiveSession;
    }

    private void ApplyModelSelection(StartupModelSelection selection, string banner, bool syncActiveSession = true)
    {
        if (selection.Models.Count == 0)
            throw new InvalidOperationException("No models configured. Use launch settings or select a model configuration from the catalog.");

        _brokerProvider?.Dispose();

        CurrentSelection = selection;
        Models = selection.Models;

        var primaryModel = Models[0];
        var baseUrl = string.IsNullOrWhiteSpace(primaryModel.BaseUrl) ? "http://localhost:8080" : primaryModel.BaseUrl;
        var model = primaryModel.Model;

        var bootstrap = RoutedModelBrokerSetup.Build(Models);
        _brokerProvider = bootstrap.Provider;
        _engine = new ChatSessionEngine(new InMemorySessionStore(), bootstrap.Broker);
        Manager = new SessionManager(_repo, _engine, _storePath, _jsonOptions, _mode, model, Models);

        Console.WriteLine($"{banner}: {(selection.UsesCatalog ? "catalog" : "launch settings")} name={selection.Name} source={selection.Source}");
        Console.WriteLine($"Connecting to {baseUrl} model={model} mode={_mode}");
        Console.WriteLine($"Models configured: {ModelSettings.Describe(Models)}");

        if (syncActiveSession && !string.IsNullOrWhiteSpace(ActiveSession))
            SyncSessionWithEngine(ActiveSession);
    }

    private string CreateFreshSessionIdForSelection(StartupModelSelection selection)
    {
        var seed = string.IsNullOrWhiteSpace(selection.Name) ? Models[0].Model : selection.Name;
        var normalized = new string(seed
            .Trim()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_')
            .ToArray())
            .Trim('_');

        if (string.IsNullOrWhiteSpace(normalized))
            normalized = "session";

        var baseSid = normalized.Length <= 40 ? normalized : normalized[..40];
        var sid = baseSid;
        var suffix = 1;

        while (_repo.Sessions.ContainsKey(sid))
        {
            suffix++;
            sid = $"{baseSid}_{suffix}";
        }

        return sid;
    }

    private string ResolveStartupSessionId(StartupModelSelection selection)
    {
        var compatible = _repo.Sessions.Keys
            .OrderBy(k => k)
            .FirstOrDefault(SessionMatchesCurrentConfiguration);

        if (!string.IsNullOrWhiteSpace(compatible))
            return compatible;

        if (_repo.Sessions.Count == 0 && !selection.UsesCatalog)
            return "s1";

        return CreateFreshSessionIdForSelection(selection);
    }

    private bool SessionMatchesCurrentConfiguration(string sid)
    {
        if (!_repo.Sessions.ContainsKey(sid))
            return false;

        if (!_repo.Meta.TryGetValue(sid, out var meta) || string.IsNullOrWhiteSpace(meta.Model))
            return true;

        return Models.Any(m => string.Equals(m.Model, meta.Model, StringComparison.OrdinalIgnoreCase));
    }

    private void SyncSessionWithEngine(string sid)
    {
        Manager.Ensure(sid);

        var meta = Manager.GetMeta(sid);
        string? sys = null;
        if (_repo.Sessions.TryGetValue(sid, out var list))
            sys = list.FirstOrDefault(m => m.Role == "system")?.Content;

        var model = Manager.GetModelForSession(sid);
        _engine.CreateOrGet(sid, model, systemPrompt: sys, temperature: meta.Temperature, topP: meta.TopP);
    }

    private string RequireActiveSession()
        => ActiveSession ?? throw new InvalidOperationException("No active session selected.");

    public void Dispose()
    {
        _brokerProvider?.Dispose();
    }
}



