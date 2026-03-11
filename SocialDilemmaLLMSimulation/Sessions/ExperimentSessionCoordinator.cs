using System.Text.Json;

namespace SocialDilemmaLLMSimulation;

public sealed record ExperimentTurnReply(string Reply, TimeSpan Elapsed);

public interface IRepeatedGameSessionCoordinator
{
    (string ModelA, string ModelB) ResolveRunModels(string? preferredModelA = null, string? preferredModelB = null);
    void PrepareExperimentSession(string sid, string model, string systemPrompt, bool resetIfExists);
    Task<ExperimentTurnReply> SendExperimentPromptAsync(string sid, string prompt, Func<string>? kvRenewalContextProvider = null);
    void DeleteExperimentSession(string sid);
}

public sealed class ExperimentSessionCoordinator : SessionCoordinator, IRepeatedGameSessionCoordinator
{
    public ExperimentSessionCoordinator(string storePath, JsonSerializerOptions jsonOptions, string mode)
        : base(storePath, jsonOptions, mode)
    {
    }

    public (string ModelA, string ModelB) ResolveRunModels(string? preferredModelA = null, string? preferredModelB = null)
    {
        var modelA = preferredModelA ?? (Models.Count > 0 ? Models[0].Model : Util.Env("LLM_MODEL"));
        var modelB = preferredModelB ?? Models
            .Skip(1)
            .Select(m => m.Model)
            .FirstOrDefault(m => !string.Equals(m, modelA, StringComparison.OrdinalIgnoreCase))
            ?? (Models.Count > 1 ? Models[1].Model : modelA);

        return (modelA, modelB);
    }

    public void PrepareExperimentSession(string sid, string model, string systemPrompt, bool resetIfExists)
    {
        Manager.Ensure(sid, resetIfExists, systemPrompt);
        Manager.SetModel(sid, model);
    }

    public async Task<ExperimentTurnReply> SendExperimentPromptAsync(string sid, string prompt, Func<string>? kvRenewalContextProvider = null)
    {
        var reply = await Manager.SendTimedAsync(sid, prompt, kvRenewalContextProvider);
        return new ExperimentTurnReply(reply.Reply, reply.Elapsed);
    }

    public void DeleteExperimentSession(string sid)
    {
        Manager.Delete(sid);
    }
}

