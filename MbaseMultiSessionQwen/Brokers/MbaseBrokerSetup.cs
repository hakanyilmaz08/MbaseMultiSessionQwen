using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mbase.Brokers;      // MbaseBroker, MbaseBrokerOptions, ModelRoute, ProviderKind
using Mbase.Abstractions;

namespace MbaseMultiSessionQwen.Brokers;

public static class MbaseBrokerSetup
{
    /// <summary>
    /// Builds a DI container with MbaseBroker registered and returns both the ServiceProvider and IModelBroker.
    /// </summary>
    public static (ServiceProvider Provider, IModelBroker Broker) Build(string baseUrlForCompatServer)
    {
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
            o.TimeoutSeconds = 60;
            o.DefaultMaxTokens = 1024;
            o.Routes = new(StringComparer.OrdinalIgnoreCase) // <= case-insensitive
            {
                ["Qwen2.5 7 B Instruct"] = new ModelRoute   // typo guard? remove if not needed
                {
                    Provider = ProviderKind.OpenAICompat,
                    BaseUrl = baseUrlForCompatServer,
                    KvParamName = "slot_id"
                },
                ["Qwen2.5 7B Instruct"] = new ModelRoute     // <-- EXACT model id from /v1/models
                {
                    Provider = ProviderKind.OpenAICompat,
                    BaseUrl = baseUrlForCompatServer,
                    KvParamName = "slot_id"
                },
                ["qwen2.5-7b-instruct"] = new ModelRoute     // handy alias
                {
                    Provider = ProviderKind.OpenAICompat,
                    BaseUrl = baseUrlForCompatServer,
                    KvParamName = "slot_id"
                },
                ["gemma-2-9b-it"] = new ModelRoute   // typo guard? remove if not needed
                {
                    Provider = ProviderKind.OpenAICompat,
                    BaseUrl = baseUrlForCompatServer,
                    KvParamName = "slot_id"
                },
                ["Meta Llama 3.1 8B Instruct"] = new ModelRoute   // typo guard? remove if not needed
                {
                    Provider = ProviderKind.OpenAICompat,
                    BaseUrl = baseUrlForCompatServer,
                    KvParamName = "slot_id"
                }
            };
        });


        // Broker
        services.AddSingleton<IModelBroker, MbaseBroker>();

        var sp = services.BuildServiceProvider();
        var broker = sp.GetRequiredService<IModelBroker>();
        return (sp, broker);
    }
}
