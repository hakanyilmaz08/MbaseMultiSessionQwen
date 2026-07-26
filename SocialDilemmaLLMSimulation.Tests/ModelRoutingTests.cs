using SocialDilemmaLLMSimulation;
using SocialDilemmaLLMSimulation.Brokers;
using Xunit;

public sealed class ModelRoutingTests
{
    [Fact]
    public void SameModelNameCanHaveDistinctProfileRoutes()
    {
        var profiles = new[]
        {
            Profile("local", "shared-model", "http://localhost:8001", "openai-compat"),
            Profile("remote", "shared-model", "https://provider.example", "openrouter")
        };

        var options = RoutedModelBrokerSetup.CreateOptions(profiles);

        Assert.Equal(2, options.Routes.Count);
        Assert.Equal("shared-model", options.Routes["local"].Model);
        Assert.Equal("shared-model", options.Routes["remote"].Model);
        Assert.Equal("http://localhost:8001", options.Routes["local"].BaseUrl);
        Assert.Equal("https://provider.example", options.Routes["remote"].BaseUrl);
        Assert.Equal(ProviderKind.OpenAICompat, options.Routes["local"].Provider);
        Assert.Equal(ProviderKind.OpenRouter, options.Routes["remote"].Provider);
        Assert.Equal(
            "local_shared-model_vs_remote_shared-model",
            RepeatedGameRunnerBase.BuildRunLabel(profiles[0], profiles[1]));
    }

    [Fact]
    public void DuplicateProfileKeysAreRejectedBeforeRoutingStarts()
    {
        var profiles = new[]
        {
            Profile("duplicate", "model-a", "http://localhost:8001", "openai-compat"),
            Profile("duplicate", "model-b", "http://localhost:8002", "ollama")
        };

        var error = Assert.Throws<InvalidOperationException>(
            () => RoutedModelBrokerSetup.CreateOptions(profiles));

        Assert.Contains("Duplicate model profile key", error.Message);
    }

    private static ModelProfile Profile(
        string key,
        string model,
        string baseUrl,
        string provider)
        => new()
        {
            Key = key,
            Model = model,
            BaseUrl = baseUrl,
            Provider = provider,
            Temperature = 0.7,
            TopP = 0.95
        };
}
