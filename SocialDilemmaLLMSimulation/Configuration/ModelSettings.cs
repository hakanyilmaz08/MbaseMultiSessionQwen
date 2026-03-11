using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace SocialDilemmaLLMSimulation;

public sealed record ModelReasoningSettings
{
    public bool? Enabled { get; init; }
    public string? Effort { get; init; }
    public int? MaxTokens { get; init; }
    public bool? Exclude { get; init; }

    public bool HasValue
        => Enabled is not null
        || !string.IsNullOrWhiteSpace(Effort)
        || MaxTokens is not null
        || Exclude is not null;

    public Dictionary<string, object> ToPayload()
    {
        var payload = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        if (Enabled is not null)
            payload["enabled"] = Enabled.Value;
        if (!string.IsNullOrWhiteSpace(Effort))
            payload["effort"] = Effort.Trim().ToLowerInvariant();
        if (MaxTokens is not null)
            payload["max_tokens"] = MaxTokens.Value;
        if (Exclude is not null)
            payload["exclude"] = Exclude.Value;

        return payload;
    }

    public static ModelReasoningSettings? Normalize(ModelReasoningSettings? reasoning)
    {
        if (reasoning is null)
            return null;

        var normalized = reasoning with
        {
            Effort = string.IsNullOrWhiteSpace(reasoning.Effort) ? null : reasoning.Effort.Trim().ToLowerInvariant(),
            MaxTokens = reasoning.MaxTokens is > 0 ? reasoning.MaxTokens : null
        };

        return normalized.HasValue ? normalized : null;
    }
}

public sealed record ModelProfile
{
    public required string Key { get; init; }
    public required string Model { get; init; }
    public string BaseUrl { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public string? ApiKeyEnv { get; init; }
    public string Provider { get; init; } = "openai-compat";
    public string Source { get; init; } = "local";
    public string CredentialKey { get; init; } = "";
    public bool RequiresApiKey { get; init; }
    public required double Temperature { get; init; }
    public required double TopP { get; init; }
    public int? MaxTokens { get; init; }
    public ModelReasoningSettings? Reasoning { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    public override string ToString()
        => string.IsNullOrWhiteSpace(Key) ? Model : $"{Key}:{Model}";
}

public sealed record StartupModelSelection(string Name, string Source, IReadOnlyList<ModelProfile> Models, bool UsesCatalog);

public sealed class ModelConfigurationCatalog
{
    public List<ModelConfigurationOption> Configurations { get; set; } = new();
}

public sealed class ModelConfigurationOption
{
    public string Name { get; set; } = "";
    public string Source { get; set; } = "local";
    public List<ModelConfigurationEntry> Models { get; set; } = new();

    public string ProviderSummary()
        => string.Join(" vs ",
            Models.Select(m => m.ProviderLabel())
                .Distinct(StringComparer.OrdinalIgnoreCase));

    public string ModelSummary()
        => string.Join(" vs ",
            Models.Select(m => m.Model)
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct(StringComparer.OrdinalIgnoreCase));

    public StartupModelSelection ToSelection()
    {
        var source = NormalizeSource(Source);
        var resolvedModels = Models
            .Where(m => !string.IsNullOrWhiteSpace(m.Model))
            .Select(m => m.ToModelProfile(source))
            .ToList();

        if (resolvedModels.Count == 0)
            throw new InvalidOperationException($"Model configuration '{Name}' does not define any models.");

        return new StartupModelSelection(
            Name: string.IsNullOrWhiteSpace(Name) ? "Unnamed configuration" : Name.Trim(),
            Source: source,
            Models: resolvedModels,
            UsesCatalog: true);
    }

    private static string NormalizeSource(string? source)
        => string.IsNullOrWhiteSpace(source) ? "local" : source.Trim().ToLowerInvariant();
}

public sealed class ModelConfigurationEntry
{
    public string Key { get; set; } = "";
    public string Model { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string Provider { get; set; } = "openai-compat";
    public string? ApiKey { get; set; }
    public string? ApiKeyEnv { get; set; }
    public string? CredentialKey { get; set; }
    public bool? RequiresApiKey { get; set; }
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public int? MaxTokens { get; set; }
    public ModelReasoningSettings? Reasoning { get; set; }
    public Dictionary<string, string>? Headers { get; set; }

    public string ProviderLabel()
        => string.IsNullOrWhiteSpace(Provider) ? "openai-compat" : Provider.Trim();

    public ModelProfile ToModelProfile(string source)
    {
        var normalizedProvider = ProviderLabel();
        var normalizedSource = string.IsNullOrWhiteSpace(source) ? "local" : source.Trim().ToLowerInvariant();
        var requiresApiKey = RequiresApiKey ?? InferRequiresApiKey(normalizedSource, normalizedProvider);

        return new ModelProfile
        {
            Key = string.IsNullOrWhiteSpace(Key) ? "primary" : Key.Trim(),
            Model = Model.Trim(),
            BaseUrl = BaseUrl.Trim(),
            ApiKey = ApiKey ?? "",
            ApiKeyEnv = string.IsNullOrWhiteSpace(ApiKeyEnv) ? null : ApiKeyEnv.Trim(),
            Provider = normalizedProvider,
            Source = normalizedSource,
            CredentialKey = ResolveCredentialKey(CredentialKey, normalizedProvider),
            RequiresApiKey = requiresApiKey,
            Temperature = RequireConfiguredTemperature(Temperature, Model.Trim()),
            TopP = RequireConfiguredTopP(TopP, Model.Trim()),
            MaxTokens = MaxTokens is > 0 ? MaxTokens : null,
            Reasoning = ModelReasoningSettings.Normalize(Reasoning),
            Headers = NormalizeHeaders(Headers)
        };
    }

    private static string ResolveCredentialKey(string? credentialKey, string provider)
        => SecureCredentialStore.NormalizeCredentialKey(
            string.IsNullOrWhiteSpace(credentialKey) ? provider : credentialKey);

    private static bool InferRequiresApiKey(string source, string provider)
    {
        if (!string.Equals(source, "external", StringComparison.OrdinalIgnoreCase))
            return false;

        return provider.Trim().ToLowerInvariant() switch
        {
            "ollama" => false,
            "llama" => false,
            "llama.cpp" => false,
            "llamacpp" => false,
            _ => true
        };
    }

    private static double RequireConfiguredTemperature(double? temperature, string model)
        => RequireConfiguredSamplingValue(temperature, nameof(Temperature), model, min: 0.0, max: 2.0);

    private static double RequireConfiguredTopP(double? topP, string model)
        => RequireConfiguredSamplingValue(topP, nameof(TopP), model, min: 0.0, max: 1.0);

    private static double RequireConfiguredSamplingValue(double? value, string fieldName, string model, double min, double max)
    {
        if (value.HasValue && value.Value >= min && value.Value <= max)
            return value.Value;

        throw new InvalidOperationException($"Model '{model}' must define {fieldName} in model-configurations.json with a value between {min} and {max}.");
    }

    private static IReadOnlyDictionary<string, string> NormalizeHeaders(Dictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
            return new Dictionary<string, string>();

        return headers
            .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
            .ToDictionary(
                kvp => kvp.Key.Trim(),
                kvp => kvp.Value.Trim(),
                StringComparer.OrdinalIgnoreCase);
    }
}

public static class ModelSettings
{
    public static StartupModelSelection CreateLaunchSelection()
    {
        return new StartupModelSelection(
            Name: "Launch settings",
            Source: "local",
            Models: LoadFromEnvironment(),
            UsesCatalog: false);
    }

    public static StartupModelSelection ResolveStartupSelection()
    {
        var launchSelection = CreateLaunchSelection();
        Console.Write("Opt into external/config catalog selection instead of launch settings? [y/N]: ");
        var choice = (Console.ReadLine() ?? string.Empty).Trim();
        if (!IsYes(choice))
            return launchSelection;

        var selected = PromptForConfigurationSelection(includeLaunchSelection: false, launchSelection: null, allowCancel: false);
        if (selected is null)
        {
            Console.WriteLine($"No configurations found at '{ResolveCatalogPath()}'. Falling back to launch settings.");
            return launchSelection;
        }

        return selected;
    }

    public static IReadOnlyList<ModelProfile> LoadFromEnvironment()
    {
        var profiles = new List<ModelProfile>();

        var primaryModel = Util.Env("LLM_MODEL");
        if (!string.IsNullOrWhiteSpace(primaryModel))
        {
            profiles.Add(new ModelProfile
            {
                Key = "primary",
                Model = primaryModel,
                BaseUrl = Util.Env("LLM_BASE_URL"),
                ApiKey = Util.Env("LLM_API_KEY"),
                ApiKeyEnv = "LLM_API_KEY",
                Provider = NormalizeProvider(Util.DetectEnv("LLM_PROVIDER_PRIMARY", Util.DetectEnv("LLM_PROVIDER", "openai-compat"))),
                Source = "local",
                CredentialKey = "local-primary",
                RequiresApiKey = false,
                Temperature = RequireConfiguredLaunchDouble(Util.DetectEnv("LLM_TEMPERATURE_PRIMARY", Util.Env("LLM_TEMPERATURE")), "LLM_TEMPERATURE_PRIMARY", "LLM_TEMPERATURE", primaryModel, min: 0.0, max: 2.0),
                TopP = RequireConfiguredLaunchDouble(Util.DetectEnv("LLM_TOP_P_PRIMARY", Util.Env("LLM_TOP_P")), "LLM_TOP_P_PRIMARY", "LLM_TOP_P", primaryModel, min: 0.0, max: 1.0)
            });
        }

        foreach (var suffix in new[] { "A", "B" })
        {
            var model = Util.Env($"LLM_MODEL_{suffix}");
            if (string.IsNullOrWhiteSpace(model))
                continue;

            var baseUrl = Util.Env($"LLM_BASE_URL_{suffix}");
            if (string.IsNullOrWhiteSpace(baseUrl))
                baseUrl = Util.Env("LLM_BASE_URL");

            var apiKey = Util.Env($"LLM_API_KEY_{suffix}");
            if (string.IsNullOrWhiteSpace(apiKey))
                apiKey = Util.Env("LLM_API_KEY");

            profiles.Add(new ModelProfile
            {
                Key = suffix.ToLowerInvariant(),
                Model = model,
                BaseUrl = baseUrl,
                ApiKey = apiKey,
                ApiKeyEnv = string.IsNullOrWhiteSpace(apiKey) ? "LLM_API_KEY" : $"LLM_API_KEY_{suffix}",
                Provider = NormalizeProvider(Util.DetectEnv($"LLM_PROVIDER_{suffix}", Util.DetectEnv("LLM_PROVIDER", "openai-compat"))),
                Source = "local",
                CredentialKey = $"local-{suffix.ToLowerInvariant()}",
                RequiresApiKey = false,
                Temperature = RequireConfiguredLaunchDouble(Util.DetectEnv($"LLM_TEMPERATURE_{suffix}", Util.Env("LLM_TEMPERATURE")), $"LLM_TEMPERATURE_{suffix}", "LLM_TEMPERATURE", model, min: 0.0, max: 2.0),
                TopP = RequireConfiguredLaunchDouble(Util.DetectEnv($"LLM_TOP_P_{suffix}", Util.Env("LLM_TOP_P")), $"LLM_TOP_P_{suffix}", "LLM_TOP_P", model, min: 0.0, max: 1.0)
            });
        }

        return profiles;
    }

    public static string Describe(IReadOnlyList<ModelProfile> profiles)
    {
        if (profiles.Count == 0) return "(none)";

        return string.Join(", ",
            profiles.Select(p => $"{p.Model} [{p.Provider}/{p.Source}]"));
    }

    public static StartupModelSelection? PromptForConfigurationSelection(
        bool includeLaunchSelection,
        StartupModelSelection? launchSelection,
        bool allowCancel)
    {
        var selections = new List<StartupModelSelection>();
        if (includeLaunchSelection && launchSelection is not null)
            selections.Add(launchSelection);

        selections.AddRange(LoadCatalogSelections());

        if (selections.Count == 0)
            return null;

        Console.WriteLine();
        Console.WriteLine("Available model configurations:");
        PrintConfigurationTable(selections);

        if (allowCancel)
            Console.WriteLine("Press Enter to keep the current configuration.");

        while (true)
        {
            Console.Write("Select configuration #: ");
            var selection = (Console.ReadLine() ?? string.Empty).Trim();

            if (allowCancel && selection.Length == 0)
                return null;

            if (int.TryParse(selection, out var index) && index >= 1 && index <= selections.Count)
            {
                var prepared = PrepareSelectionForUse(selections[index - 1]);
                if (prepared is not null)
                    return prepared;
                continue;
            }

            var byName = selections.FirstOrDefault(c =>
                string.Equals(c.Name, selection, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
            {
                var prepared = PrepareSelectionForUse(byName);
                if (prepared is not null)
                    return prepared;
                continue;
            }

            Console.WriteLine("Invalid selection. Enter the configuration number or exact name.");
        }
    }

    private static StartupModelSelection? PrepareSelectionForUse(StartupModelSelection selection)
    {
        if (!selection.Models.Any(m => m.RequiresApiKey))
            return selection;

        var resolvedKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var updatedModels = new List<ModelProfile>(selection.Models.Count);

        foreach (var model in selection.Models)
        {
            if (!model.RequiresApiKey)
            {
                updatedModels.Add(model);
                continue;
            }

            if (!resolvedKeys.TryGetValue(model.CredentialKey, out var apiKey))
            {
                if (!TryResolveApiKey(model, out apiKey))
                    return null;

                resolvedKeys[model.CredentialKey] = apiKey;
            }

            updatedModels.Add(model with { ApiKey = apiKey });
        }

        return selection with { Models = updatedModels };
    }

    private static bool TryResolveApiKey(ModelProfile model, out string apiKey)
    {
        var providerName = FormatProviderName(model.Provider);
        var credentialKey = string.IsNullOrWhiteSpace(model.CredentialKey)
            ? SecureCredentialStore.NormalizeCredentialKey(model.Provider)
            : model.CredentialKey;

        var hasStoredKey = SecureCredentialStore.TryRead(credentialKey, out var storedKey, out _);
        var envKey = string.IsNullOrWhiteSpace(model.ApiKeyEnv)
            ? null
            : Environment.GetEnvironmentVariable(model.ApiKeyEnv);
        var inlineKey = string.IsNullOrWhiteSpace(model.ApiKey) || string.Equals(model.ApiKey, "EMPTY", StringComparison.OrdinalIgnoreCase)
            ? null
            : model.ApiKey;

        if (hasStoredKey)
        {
            Console.Write($"Use the stored API key for {providerName} from {SecureCredentialStore.StoreDisplayName}? [Y/n]: ");
            var useStoredChoice = (Console.ReadLine() ?? string.Empty).Trim();
            if (!IsNo(useStoredChoice))
            {
                apiKey = storedKey;
                Console.WriteLine($"Using securely stored API key for {providerName} from {SecureCredentialStore.StoreDisplayName}.");
                return true;
            }
        }

        var prompt = $"Enter a new API key for {providerName} (input visible; paste supported)";
        if (!string.IsNullOrWhiteSpace(envKey) && !string.IsNullOrWhiteSpace(model.ApiKeyEnv))
            prompt += $" (press Enter to use {model.ApiKeyEnv} for this run)";
        else if (!string.IsNullOrWhiteSpace(inlineKey))
            prompt += " (press Enter to use configured fallback for this run)";

        prompt += ": ";

        var entered = ReadSecret(prompt);
        if (!string.IsNullOrWhiteSpace(entered))
        {
            apiKey = entered;
        }
        else if (!string.IsNullOrWhiteSpace(envKey))
        {
            apiKey = envKey;
            Console.WriteLine($"Using {model.ApiKeyEnv} for this run.");
            return true;
        }
        else if (!string.IsNullOrWhiteSpace(inlineKey))
        {
            apiKey = inlineKey;
            Console.WriteLine($"Using the configured non-secure API key for {providerName} for this run.");
            return true;
        }
        else
        {
            Console.WriteLine($"Selection canceled. No API key entered for {providerName}.");
            apiKey = string.Empty;
            return false;
        }

        if (!SecureCredentialStore.IsSupported)
        {
            Console.WriteLine($"Secure credential storage is not available on this OS. The {providerName} API key will be used for this run only.");
            return true;
        }

        Console.Write($"Store this new {providerName} API key securely in {SecureCredentialStore.StoreDisplayName}? [y/N]: ");
        var storeChoice = (Console.ReadLine() ?? string.Empty).Trim();
        if (!IsYes(storeChoice))
        {
            Console.WriteLine($"The {providerName} API key will be used for this run only.");
            return true;
        }

        if (!SecureCredentialStore.TryWrite(credentialKey, apiKey, out var storeError))
        {
            Console.WriteLine($"Failed to store API key for {providerName}: {storeError}");
            Console.WriteLine($"The {providerName} API key will be used for this run only.");
            return true;
        }

        Console.WriteLine($"{providerName} API key stored in {SecureCredentialStore.StoreDisplayName}.");
        return true;
    }

    private static List<ModelConfigurationOption> LoadCatalog(string path)
    {
        if (!File.Exists(path))
            return new List<ModelConfigurationOption>();

        using var fs = File.OpenRead(path);
        var catalog = JsonSerializer.Deserialize<ModelConfigurationCatalog>(fs, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return catalog?.Configurations
            .Where(c => c.Models is not null && c.Models.Count > 0)
            .ToList()
            ?? new List<ModelConfigurationOption>();
    }

    private static IReadOnlyList<StartupModelSelection> LoadCatalogSelections()
        => LoadCatalog(ResolveCatalogPath())
            .Select(c => c.ToSelection())
            .ToList();

    private static string ResolveCatalogPath()
    {
        var configured = Util.Env("MBASE_MODEL_CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.IsPathRooted(configured)
                ? configured
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured));
        }

        return Path.Combine(AppContext.BaseDirectory, "model-configurations.json");
    }

    private static void PrintConfigurationTable(IReadOnlyList<StartupModelSelection> configs)
    {
        var rows = configs.Select((config, index) => new[]
        {
            (index + 1).ToString(),
            config.Name,
            string.IsNullOrWhiteSpace(config.Source) ? "local" : config.Source,
            string.Join(" vs ",
                config.Models.Select(m => m.Provider)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)),
            string.Join(" vs ",
                config.Models.Select(m => m.Model)
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .Distinct(StringComparer.OrdinalIgnoreCase))
        }).ToList();

        var headers = new[] { "#", "Name", "Source", "Provider", "Models" };
        var widths = headers
            .Select((header, idx) => Math.Min(
                Math.Max(header.Length, rows.Select(r => r[idx].Length).DefaultIfEmpty(0).Max()),
                idx == 4 ? 80 : 36))
            .ToArray();

        PrintRow(headers, widths);
        PrintRow(widths.Select(w => new string('-', w)).ToArray(), widths);

        foreach (var row in rows)
            PrintRow(row, widths);
    }

    private static void PrintRow(IReadOnlyList<string> columns, IReadOnlyList<int> widths)
    {
        var padded = columns
            .Select((value, idx) => Pad(value, widths[idx]))
            .ToArray();

        Console.WriteLine(string.Join(" | ", padded));
    }

    private static string Pad(string value, int width)
    {
        if (value.Length <= width)
            return value.PadRight(width);

        return width <= 3
            ? value[..width]
            : value[..(width - 3)] + "...";
    }

    private static bool IsYes(string value)
        => value.Equals("y", StringComparison.OrdinalIgnoreCase)
        || value.Equals("yes", StringComparison.OrdinalIgnoreCase);

    private static bool IsNo(string value)
        => value.Equals("n", StringComparison.OrdinalIgnoreCase)
        || value.Equals("no", StringComparison.OrdinalIgnoreCase);

    private static double RequireConfiguredLaunchDouble(string? raw, string primaryEnvName, string fallbackEnvName, string model, double min, double max)
    {
        if (!string.IsNullOrWhiteSpace(raw)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && parsed >= min
            && parsed <= max)
        {
            return parsed;
        }

        throw new InvalidOperationException($"Model '{model}' requires {primaryEnvName} or {fallbackEnvName} in launchSettings.json with a value between {min} and {max}.");
    }

    private static string FormatProviderName(string provider)
        => provider.Trim().ToLowerInvariant() switch
        {
            "openai" => "OpenAI",
            "openrouter" => "OpenRouter",
            "openai-compat" => "OpenAI-compatible provider",
            "ollama" => "Ollama",
            "llama" => "llama.cpp",
            "llama.cpp" => "llama.cpp",
            "llamacpp" => "llama.cpp",
            _ => provider
        };

    private static string ReadSecret(string prompt)
    {
        Console.Write(prompt);
        return (Console.ReadLine() ?? string.Empty).Trim();
    }

    private static string NormalizeProvider(string provider)
        => string.IsNullOrWhiteSpace(provider) ? "openai-compat" : provider.Trim().ToLowerInvariant();
}




