using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mbase.Brokers;      // MbaseBroker, MbaseBrokerOptions, ModelRoute, ProviderKind
using Mbase.Abstractions;
using System;
using System.Collections.Generic;

namespace MbaseMultiSessionQwen.Brokers;

public static class MbaseBrokerSetup
{
    /// <summary>
    /// Builds a DI container with MbaseBroker registered and returns both the ServiceProvider and IModelBroker.
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


        // Named HttpClients used by MbaseBroker
        services.AddHttpClient("OpenAI");
        services.AddHttpClient("OpenAICompat");
        services.AddHttpClient("Ollama");

        // Minimal routing config – adjust to your models/backends
        services.Configure<MbaseBrokerOptions>(o =>
        {
            o.TimeoutSeconds = 600;
            o.DefaultMaxTokens = 1024;
            o.Routes = new(StringComparer.OrdinalIgnoreCase);

            var defaultBase = string.IsNullOrWhiteSpace(models[0].BaseUrl)
                ? "http://localhost:8080"
                : models[0].BaseUrl;

            foreach (var model in models)
            {
                var baseUrl = string.IsNullOrWhiteSpace(model.BaseUrl) ? defaultBase : model.BaseUrl;
                o.Routes[model.Model] = new ModelRoute
                {
                    Provider = ProviderKind.OpenAICompat,
                    BaseUrl = baseUrl,
                    KvParamName = "slot_id"
                };
            }
        });


// Broker
        services.AddSingleton<IModelBroker, MbaseBroker>();

        var sp = services.BuildServiceProvider();
        var broker = sp.GetRequiredService<IModelBroker>();
        return (sp, broker);
    }
}
