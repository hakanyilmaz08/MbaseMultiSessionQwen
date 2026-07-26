using System.Text.Json;
using SocialDilemmaLLMSimulation.Domain;
using Xunit;

public sealed class SessionRepoTests
{
    [Fact]
    public void MissingStoreLoadsAsEmpty()
    {
        using var directory = new TestDirectory();

        var loaded = SessionRepo.Load(directory.SessionPath, JsonOptions());

        Assert.Empty(loaded.Sessions);
        Assert.Empty(loaded.Meta);
        Assert.Empty(loaded.ConversationIds);
    }

    [Fact]
    public void CurrentStoreLoadsAllSections()
    {
        using var directory = new TestDirectory();
        var current = new SessionRepo(
            new Dictionary<string, List<Message>>
            {
                ["current"] = new() { new Message("user", "hello") }
            },
            new Dictionary<string, SessionMeta>
            {
                ["current"] = new SessionMeta("current", 0.7, 0.95, "model")
            },
            new Dictionary<string, string?>
            {
                ["current"] = "engine-current"
            });
        File.WriteAllText(
            directory.SessionPath,
            JsonSerializer.Serialize(current, JsonOptions()));

        var loaded = SessionRepo.Load(directory.SessionPath, JsonOptions());

        Assert.Equal("hello", Assert.Single(loaded.Sessions["current"]).Content);
        Assert.Equal("model", loaded.Meta["current"].Model);
        Assert.Equal("engine-current", loaded.ConversationIds["current"]);
    }

    [Fact]
    public void LegacyDictionaryStoreStillLoads()
    {
        using var directory = new TestDirectory();
        var legacy = new Dictionary<string, List<Message>>
        {
            ["legacy"] = new() { new Message("assistant", "preserved") }
        };
        File.WriteAllText(
            directory.SessionPath,
            JsonSerializer.Serialize(legacy, JsonOptions()));

        var loaded = SessionRepo.Load(directory.SessionPath, JsonOptions());

        Assert.Equal("preserved", Assert.Single(loaded.Sessions["legacy"]).Content);
        Assert.Empty(loaded.Meta);
        Assert.Empty(loaded.ConversationIds);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("""{"sessions":{"broken":null}}""")]
    [InlineData("""{"sessions":{"broken":[null]}}""")]
    [InlineData("""{"sessions":{},"meta":{"broken":null}}""")]
    public void MalformedStoreThrowsWithoutChangingOriginalFile(string contents)
    {
        using var directory = new TestDirectory();
        File.WriteAllText(directory.SessionPath, contents);
        var originalBytes = File.ReadAllBytes(directory.SessionPath);

        var exception = Assert.Throws<InvalidDataException>(
            () => SessionRepo.Load(directory.SessionPath, JsonOptions()));

        Assert.Contains("was not modified", exception.Message);
        Assert.Equal(originalBytes, File.ReadAllBytes(directory.SessionPath));
    }

    private static JsonSerializerOptions JsonOptions()
        => new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = Directory.CreateTempSubdirectory("mbase-session-repo-tests-").FullName;
        }

        public string Path { get; }
        public string SessionPath => System.IO.Path.Combine(Path, "sessions.json");

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
