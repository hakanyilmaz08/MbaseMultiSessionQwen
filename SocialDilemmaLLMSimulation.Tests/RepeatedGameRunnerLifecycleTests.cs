using System.Text.RegularExpressions;
using SocialDilemmaLLMSimulation;
using Xunit;

public sealed class RepeatedGameRunnerLifecycleTests
{
    [Fact]
    public async Task FailedStandardRunDeletesBothGameScopedExecutionSessions()
    {
        var pdCoordinator = new ThrowingExperimentCoordinator();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new IPDRunner(pdCoordinator).RunV1ToV5SequentialAsync(
                baseSessionPrefix: "",
                rounds: 1,
                clearSessions: true));

        AssertSessionLifecycle(pdCoordinator, "PD");

        var sdCoordinator = new ThrowingExperimentCoordinator();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ISDRunner(sdCoordinator).RunV1ToV5SequentialAsync(
                baseSessionPrefix: "",
                rounds: 1,
                clearSessions: true));

        AssertSessionLifecycle(sdCoordinator, "SD");
        Assert.NotEqual(pdCoordinator.PreparedSessions[0], sdCoordinator.PreparedSessions[0]);
    }

    private static void AssertSessionLifecycle(
        ThrowingExperimentCoordinator coordinator,
        string gameCode)
    {
        Assert.Equal(2, coordinator.PreparedSessions.Count);
        Assert.Equal(
            coordinator.PreparedSessions.OrderBy(s => s),
            coordinator.DeletedSessions.OrderBy(s => s));

        foreach (var sessionId in coordinator.PreparedSessions)
        {
            Assert.Contains($"_{gameCode}_exec", sessionId);
            Assert.Matches(
                new Regex($"_{gameCode}_exec[0-9a-f]{{12}}_run1_v1_[AB]$"),
                sessionId);
        }
    }

    private sealed class ThrowingExperimentCoordinator : IRepeatedGameSessionCoordinator
    {
        public List<string> PreparedSessions { get; } = new();
        public List<string> DeletedSessions { get; } = new();

        public (string ModelA, string ModelB) ResolveRunModels(
            string? preferredModelA = null,
            string? preferredModelB = null)
            => (preferredModelA ?? "model-a", preferredModelB ?? "model-b");

        public void PrepareExperimentSession(
            string sid,
            string model,
            string systemPrompt,
            bool resetIfExists)
            => PreparedSessions.Add(sid);

        public Task<ExperimentTurnReply> SendExperimentPromptAsync(
            string sid,
            string prompt,
            Func<string>? kvRenewalContextProvider = null)
            => Task.FromException<ExperimentTurnReply>(
                new InvalidOperationException("Expected experiment failure."));

        public void DeleteExperimentSession(string sid)
            => DeletedSessions.Add(sid);
    }
}
