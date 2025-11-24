using System.Collections.Generic;
using System.Linq;

public record ModelProfile(string Key, string Model, string BaseUrl, string ApiKey)
{
    public override string ToString()
        => string.IsNullOrWhiteSpace(Key) ? Model : $"{Key}:{Model}";
}

public static class ModelSettings
{
    public static IReadOnlyList<ModelProfile> Load()
    {
        var profiles = new List<ModelProfile>();

        var primaryModel = Util.Env("LLM_MODEL");
        if (!string.IsNullOrWhiteSpace(primaryModel))
        {
            profiles.Add(new ModelProfile(
                Key: "primary",
                Model: primaryModel,
                BaseUrl: Util.Env("LLM_BASE_URL"),
                ApiKey: Util.Env("LLM_API_KEY")));
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

            profiles.Add(new ModelProfile(
                Key: suffix.ToLowerInvariant(),
                Model: model,
                BaseUrl: baseUrl,
                ApiKey: apiKey));
        }

        return profiles;
    }

    public static string Describe(IReadOnlyList<ModelProfile> profiles)
    {
        if (profiles.Count == 0) return "(none)";
        return string.Join(", ", profiles.Select(p => p.ToString()));
    }
}
