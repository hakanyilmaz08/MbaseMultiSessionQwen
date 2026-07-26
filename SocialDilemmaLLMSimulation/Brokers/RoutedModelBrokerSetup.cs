using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SocialDilemmaLLMSimulation.Abstractions;

namespace SocialDilemmaLLMSimulation.Brokers;

public static class RoutedModelBrokerSetup
{
    public static (ServiceProvider Provider, IModelBroker Broker) Build(
        IReadOnlyList<ModelProfile> models)
    {
        var routeOptions = CreateOptions(models);
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddConsole();
            builder.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
            builder.AddFilter("System.Net.Http.HttpClient.OpenAICompat", LogLevel.Warning);
            builder.AddFilter("System.Net.Http", LogLevel.Warning);
        });

        services.AddHttpClient("OpenAI");
        services.AddHttpClient("OpenAICompat");
        services.AddHttpClient("OpenRouter");
        services.AddHttpClient("Ollama");

        services.Configure<RoutedModelBrokerOptions>(options =>
        {
            options.TimeoutSeconds = routeOptions.TimeoutSeconds;
            options.DefaultMaxTokens = routeOptions.DefaultMaxTokens;
            options.Routes = routeOptions.Routes;
        });
        services.AddSingleton<IModelBroker, RoutedModelBroker>();

        var provider = services.BuildServiceProvider();
        return (provider, provider.GetRequiredService<IModelBroker>());
    }

    public static RoutedModelBrokerOptions CreateOptions(IReadOnlyList<ModelProfile> models)
    {
        if (models is null || models.Count == 0)
            throw new ArgumentException("At least one model profile is required.", nameof(models));

        var options = new RoutedModelBrokerOptions
        {
            TimeoutSeconds = 1200,
            DefaultMaxTokens = 1024,
            Routes = new Dictionary<string, ModelRoute>(StringComparer.OrdinalIgnoreCase)
        };
        var defaultBase = string.IsNullOrWhiteSpace(models[0].BaseUrl)
            ? "http://localhost:8080"
            : models[0].BaseUrl;

        foreach (var profile in models)
        {
            var profileKey = profile.Key.Trim();
            if (string.IsNullOrWhiteSpace(profileKey))
                throw new InvalidOperationException($"Model '{profile.Model}' has an empty profile key.");
            if (options.Routes.ContainsKey(profileKey))
                throw new InvalidOperationException($"Duplicate model profile key '{profileKey}'.");

            var provider = ResolveProvider(profile.Provider);
            options.Routes.Add(profileKey, new ModelRoute
            {
                Model = profile.Model,
                Provider = provider,
                BaseUrl = string.IsNullOrWhiteSpace(profile.BaseUrl) ? defaultBase : profile.BaseUrl,
                ApiKey = profile.ApiKey,
                KvParamName = provider == ProviderKind.LlamaCpp ? "slot_id" : null,
                MaxTokens = profile.MaxTokens,
                Reasoning = ModelReasoningSettings.Normalize(profile.Reasoning),
                Headers = profile.Headers?.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            });
        }

        return options;
    }

    private static ProviderKind ResolveProvider(string providerName)
        => providerName.Trim().ToLowerInvariant() switch
        {
            "openai" => ProviderKind.OpenAI,
            "openrouter" => ProviderKind.OpenRouter,
            "ollama" => ProviderKind.Ollama,
            "llama" => ProviderKind.LlamaCpp,
            "llama.cpp" => ProviderKind.LlamaCpp,
            "llamacpp" => ProviderKind.LlamaCpp,
            _ => ProviderKind.OpenAICompat
        };
}
