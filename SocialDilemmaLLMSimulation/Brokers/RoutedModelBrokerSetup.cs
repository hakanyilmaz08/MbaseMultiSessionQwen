using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocialDilemmaLLMSimulation.Brokers;      // RoutedModelBroker, RoutedModelBrokerOptions, ModelRoute, ProviderKind
using SocialDilemmaLLMSimulation.Abstractions;
using SocialDilemmaLLMSimulation;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SocialDilemmaLLMSimulation.Brokers;

public static class RoutedModelBrokerSetup
{
    /// <summary>
    /// Builds a DI container with RoutedModelBroker registered and returns both the ServiceProvider and IModelBroker.
    /// </summary>
    public static (ServiceProvider Provider, IModelBroker Broker) Build(IReadOnlyList<ModelProfile> models)
    {
        if (models is null || models.Count == 0)
            throw new ArgumentException("At least one model profile is required", nameof(models));

        var services = new ServiceCollection();

        // Logging

        services.AddLogging(b =>
        {
            b.ClearProviders();
            b.AddConsole();

            // kill HttpClient info/trace noise
            b.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);                // all named clients
            b.AddFilter("System.Net.Http.HttpClient.OpenAICompat", LogLevel.Warning);  // (optional) just this client
            b.AddFilter("System.Net.Http", LogLevel.Warning);                           // catch any remaining
        });


        // Named HttpClients used by RoutedModelBroker
        services.AddHttpClient("OpenAI");
        services.AddHttpClient("OpenAICompat");
        services.AddHttpClient("OpenRouter");
        services.AddHttpClient("Ollama");

        // Minimal routing config â€“ adjust to your models/backends
        services.Configure<RoutedModelBrokerOptions>(o =>
        {
            o.TimeoutSeconds = 1200;
            o.DefaultMaxTokens = 1024;
            o.Routes = new(StringComparer.OrdinalIgnoreCase);

            var defaultBase = string.IsNullOrWhiteSpace(models[0].BaseUrl)
                ? "http://localhost:8080"
                : models[0].BaseUrl;

            foreach (var model in models)
            {
                var baseUrl = string.IsNullOrWhiteSpace(model.BaseUrl) ? defaultBase : model.BaseUrl;
                var provider = ResolveProvider(model.Provider);
                o.Routes[model.Model] = new ModelRoute
                {
                    Provider = provider,
                    BaseUrl = baseUrl,
                    ApiKey = model.ApiKey,
                    KvParamName = provider == ProviderKind.LlamaCpp ? "slot_id" : null,
                    MaxTokens = model.MaxTokens,
                    Reasoning = ModelReasoningSettings.Normalize(model.Reasoning),
                    Headers = model.Headers?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase)
                        ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                };
            }
        });


        // Broker
        services.AddSingleton<IModelBroker, RoutedModelBroker>();

        var sp = services.BuildServiceProvider();
        var broker = sp.GetRequiredService<IModelBroker>();
        return (sp, broker);

        static ProviderKind ResolveProvider(string providerName)
        {
            return providerName.Trim().ToLowerInvariant() switch
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
    }
}


