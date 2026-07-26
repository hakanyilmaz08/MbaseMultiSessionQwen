using System.Text.Json;
using SocialDilemmaLLMSimulation;
using SocialDilemmaLLMSimulation.Abstractions;
using SocialDilemmaLLMSimulation.Domain;
using SocialDilemmaLLMSimulation.Infrastructure;
using Xunit;

public sealed class SessionManagerTests
{
    [Fact]
    public async Task SessionRoutesByProfileKeyWhenModelNamesMatch()
    {
        using var testDirectory = new TestDirectory();
        var repo = new SessionRepo();
        var broker = new RecordingBroker();
        var profiles = new[]
        {
            Profile("first", "shared-model", 0.2),
            Profile("second", "shared-model", 0.8)
        };
        var manager = CreateManager(
            testDirectory.SessionPath,
            repo,
            broker,
            profiles,
            defaultProfileKey: "first");

        manager.Ensure("profile-session");
        manager.SetModelProfile("profile-session", "second");
        await manager.SendAsync("profile-session", "hello");

        var request = Assert.Single(broker.Requests);
        Assert.Equal("second", request.Model);
        Assert.Equal("second", manager.GetProfileKeyForSession("profile-session"));
        Assert.Equal("shared-model", manager.GetModelForSession("profile-session"));
        Assert.Equal(0.8, manager.GetMeta("profile-session").Temperature);
    }

    [Fact]
    public void LegacySessionWithoutProfileKeyKeepsItsUnambiguousModel()
    {
        using var testDirectory = new TestDirectory();
        var repo = new SessionRepo();
        repo.Sessions["legacy"] = new List<Message> { new("system", "legacy") };
        repo.Meta["legacy"] = new SessionMeta(
            "legacy",
            Temperature: 0.7,
            TopP: 0.95,
            Model: "model-b");
        var profiles = new[]
        {
            Profile("first", "model-a", 0.2),
            Profile("second", "model-b", 0.8)
        };
        var manager = CreateManager(
            testDirectory.SessionPath,
            repo,
            new RecordingBroker(),
            profiles,
            defaultProfileKey: "first");

        manager.Ensure("legacy");

        Assert.Equal("second", manager.GetProfileKeyForSession("legacy"));
        Assert.Equal("model-b", manager.GetModelForSession("legacy"));
        Assert.Equal(0.8, manager.GetMeta("legacy").Temperature);
    }

    [Fact]
    public async Task PersistedHistoryIsSentAfterEngineReplacement()
    {
        using var testDirectory = new TestDirectory();
        var firstBroker = new RecordingBroker();
        var firstManager = CreateManager(testDirectory.SessionPath, new SessionRepo(), firstBroker);

        firstManager.Ensure("session", systemPrompt: "system");
        await firstManager.SendAsync("session", "first");

        var reloadedRepo = SessionRepo.Load(testDirectory.SessionPath, JsonOptions());
        var secondBroker = new RecordingBroker();
        var secondManager = CreateManager(testDirectory.SessionPath, reloadedRepo, secondBroker);

        await secondManager.SendAsync("session", "second");

        var request = Assert.Single(secondBroker.Requests);
        Assert.Equal("system", request.SystemPrompt);
        Assert.Equal(
            new[] { "user:first", "assistant:reply-1", "user:second" },
            request.Messages.Select(m => $"{m.Role}:{m.Content}"));
    }

    [Fact]
    public async Task DeleteRemovesRepositoryHistoryAndRuntimeKvState()
    {
        using var testDirectory = new TestDirectory();
        var broker = new RecordingBroker();
        var manager = CreateManager(testDirectory.SessionPath, new SessionRepo(), broker);

        manager.Ensure("session", systemPrompt: "old-system");
        await manager.SendAsync("session", "old-message");
        manager.Delete("session");

        manager.Ensure("session", systemPrompt: "new-system");
        await manager.SendAsync("session", "new-message");

        var request = broker.Requests[^1];
        Assert.Null(request.KvHandle);
        Assert.Equal("new-system", request.SystemPrompt);
        Assert.Equal(new[] { "user:new-message" }, request.Messages.Select(m => $"{m.Role}:{m.Content}"));
    }

    [Fact]
    public async Task ResetKeepsOnlyTheRequestedSystemPromptAndClearsRuntimeState()
    {
        using var testDirectory = new TestDirectory();
        var broker = new RecordingBroker();
        var manager = CreateManager(testDirectory.SessionPath, new SessionRepo(), broker);

        manager.Ensure("session", systemPrompt: "system");
        await manager.SendAsync("session", "before-reset");

        manager.Reset("session", keepSystemPrompt: true);
        Assert.Equal(
            new[] { "system:system" },
            manager.GetHistory("session")!.Select(m => $"{m.Role}:{m.Content}"));

        await manager.SendAsync("session", "after-reset");
        var request = broker.Requests[^1];
        Assert.Null(request.KvHandle);
        Assert.Equal(new[] { "user:after-reset" }, request.Messages.Select(m => $"{m.Role}:{m.Content}"));

        manager.Reset("session", keepSystemPrompt: false);
        Assert.Empty(manager.GetHistory("session")!);
    }

    [Fact]
    public async Task FailedSendDoesNotCommitThePendingUserMessage()
    {
        using var testDirectory = new TestDirectory();
        var broker = new RecordingBroker { ThrowOnCall = 1 };
        var manager = CreateManager(testDirectory.SessionPath, new SessionRepo(), broker);

        manager.Ensure("session", systemPrompt: "system");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.SendAsync("session", "not-committed"));

        Assert.Equal(
            new[] { "system:system" },
            manager.GetHistory("session")!.Select(m => $"{m.Role}:{m.Content}"));
    }

    private static SessionManager CreateManager(
        string sessionPath,
        SessionRepo repo,
        IModelBroker broker)
    {
        var profile = new ModelProfile
        {
            Key = "test",
            Model = "test-model",
            Temperature = 0.7,
            TopP = 0.95
        };
        var engine = new ChatSessionEngine(new InMemorySessionStore(), broker);
        return new SessionManager(
            repo,
            engine,
            sessionPath,
            JsonOptions(),
            mode: "server",
            defaultProfileKey: profile.Key,
            knownModels: new[] { profile });
    }

    private static SessionManager CreateManager(
        string sessionPath,
        SessionRepo repo,
        IModelBroker broker,
        IReadOnlyList<ModelProfile> profiles,
        string defaultProfileKey)
    {
        var engine = new ChatSessionEngine(new InMemorySessionStore(), broker);
        return new SessionManager(
            repo,
            engine,
            sessionPath,
            JsonOptions(),
            mode: "server",
            defaultProfileKey,
            profiles);
    }

    private static ModelProfile Profile(string key, string model, double temperature)
        => new()
        {
            Key = key,
            Model = model,
            Temperature = temperature,
            TopP = 0.95
        };

    private static JsonSerializerOptions JsonOptions()
        => new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

    private sealed record BrokerRequest(
        string Model,
        string? SystemPrompt,
        IReadOnlyList<ChatMessage> Messages,
        string? KvHandle);

    private sealed class RecordingBroker : IModelBroker
    {
        public List<BrokerRequest> Requests { get; } = new();
        public int? ThrowOnCall { get; init; }

        public Task<(string text, (int PromptTokens, int CompletionTokens) usage, string? kvHandle)> CompleteAsync(
            string model,
            string? system,
            IReadOnlyList<ChatMessage> messages,
            double temperature,
            double topP,
            string? kvHandle,
            CancellationToken ct = default)
        {
            Requests.Add(new BrokerRequest(model, system, messages.ToList(), kvHandle));
            if (ThrowOnCall == Requests.Count)
                throw new InvalidOperationException("Expected broker failure.");

            return Task.FromResult((
                $"reply-{Requests.Count}",
                (PromptTokens: 3, CompletionTokens: 2),
                (string?)$"kv-{Requests.Count}"));
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = Directory.CreateTempSubdirectory("mbase-session-tests-").FullName;
        }

        public string Path { get; }
        public string SessionPath => System.IO.Path.Combine(Path, "sessions.json");

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
