using System.Text.Json;

namespace SocialDilemmaLLMSimulation;

public sealed record ExperimentTurnReply(string Reply, TimeSpan Elapsed);

public interface IRepeatedGameSessionCoordinator
{
    (ModelProfile ProfileA, ModelProfile ProfileB) ResolveRunModels(
        string? preferredProfileKeyA = null,
        string? preferredProfileKeyB = null);
    void PrepareExperimentSession(
        string sid,
        ModelProfile profile,
        string systemPrompt,
        bool resetIfExists);
    Task<ExperimentTurnReply> SendExperimentPromptAsync(string sid, string prompt, Func<string>? kvRenewalContextProvider = null);
    void DeleteExperimentSession(string sid);
}

public sealed class ExperimentSessionCoordinator : SessionCoordinator, IRepeatedGameSessionCoordinator
{
    public ExperimentSessionCoordinator(string storePath, JsonSerializerOptions jsonOptions, string mode)
        : base(storePath, jsonOptions, mode)
    {
    }

    public (ModelProfile ProfileA, ModelProfile ProfileB) ResolveRunModels(
        string? preferredProfileKeyA = null,
        string? preferredProfileKeyB = null)
    {
        if (Models.Count == 0)
            throw new InvalidOperationException("No model profiles are configured.");

        var profileA = ResolveProfile(preferredProfileKeyA) ?? Models[0];
        var profileB = ResolveProfile(preferredProfileKeyB)
            ?? (Models.Count > 1 ? Models[1] : profileA);

        return (profileA, profileB);
    }

    public void PrepareExperimentSession(
        string sid,
        ModelProfile profile,
        string systemPrompt,
        bool resetIfExists)
    {
        Manager.Ensure(sid, resetIfExists, systemPrompt);
        Manager.SetModelProfile(sid, profile.Key);
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

    private ModelProfile? ResolveProfile(string? keyOrLegacyModel)
    {
        if (string.IsNullOrWhiteSpace(keyOrLegacyModel))
            return null;

        return Models.FirstOrDefault(profile => string.Equals(
                   profile.Key,
                   keyOrLegacyModel,
                   StringComparison.OrdinalIgnoreCase))
            ?? Models.FirstOrDefault(profile => string.Equals(
                profile.Model,
                keyOrLegacyModel,
                StringComparison.OrdinalIgnoreCase));
    }
}
