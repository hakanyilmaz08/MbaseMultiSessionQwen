using SocialDilemmaLLMSimulation;
using Xunit;

public sealed class AdaptiveOrchestrationTests
{
    [Fact]
    public async Task AdaptiveRunPlaysAllSevenContextsAcrossTwentyRuns()
    {
        using var database = new TemporaryExperimentEnvironment();
        DbInit.EnsureCreated();
        var coordinator = new DeterministicCoordinator();
        var adaptiveRunId = AdaptiveRunLogger.Start("deterministic");
        var originalOut = Console.Out;

        AdaptiveGameResult result;
        try
        {
            Console.SetOut(TextWriter.Null);
            result = await new AdaptiveGameRunner(coordinator).RunAsync(
                baseSessionPrefix: "test",
                rounds: 1,
                experimentRunId: adaptiveRunId);
            AdaptiveRunLogger.Complete(adaptiveRunId);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Equal(140, result.GameRuns.Count);
        Assert.Equal(18, result.Selections.Count);
        Assert.All(
            result.GameRuns.GroupBy(run => run.RunId),
            run => Assert.Equal(7, run.Count()));
        Assert.All(result.GameRuns.Where(run => run.RunId == 1), run => Assert.Equal("PD", run.GameCode));
        Assert.All(result.GameRuns.Where(run => run.RunId == 2), run => Assert.Equal("SD", run.GameCode));
        Assert.All(result.GameRuns.Where(run => run.RunId >= 3), run => Assert.Equal("PD", run.GameCode));
        Assert.All(result.Selections, selection =>
        {
            Assert.Equal("PD", selection.ChoiceA);
            Assert.Equal("PD", selection.ChoiceB);
            Assert.Equal("PD", selection.ResolvedGame);
            Assert.Null(selection.RandomRoll);
        });

        Assert.Equal(
            coordinator.PreparedSessions.OrderBy(id => id),
            coordinator.DeletedSessions.OrderBy(id => id));
        Assert.Equal(316, coordinator.PreparedSessions.Count);
        Assert.Equal(
            280L,
            database.ExecuteScalar<long>(
                $"SELECT COUNT(*) FROM decisions WHERE experiment_run_id = {adaptiveRunId};"));
        Assert.Equal(
            36L,
            database.ExecuteScalar<long>(
                $"SELECT COUNT(*) FROM game_selection_decisions WHERE experiment_run_id = {adaptiveRunId};"));
        Assert.Equal(
            140L,
            database.ExecuteScalar<long>(
                $"SELECT COUNT(*) FROM decisions WHERE experiment_run_id = {adaptiveRunId} AND model_profile_key = 'local';"));
        Assert.Equal(
            140L,
            database.ExecuteScalar<long>(
                $"SELECT COUNT(*) FROM decisions WHERE experiment_run_id = {adaptiveRunId} AND model_profile_key = 'remote';"));
    }

    private sealed class DeterministicCoordinator : IRepeatedGameSessionCoordinator
    {
        private readonly ModelProfile _local = Profile("local", "shared-model");
        private readonly ModelProfile _remote = Profile("remote", "shared-model");

        public List<string> PreparedSessions { get; } = new();
        public List<string> DeletedSessions { get; } = new();

        public (ModelProfile ProfileA, ModelProfile ProfileB) ResolveRunModels(
            string? preferredProfileKeyA = null,
            string? preferredProfileKeyB = null)
            => (
                Resolve(preferredProfileKeyA, _local),
                Resolve(preferredProfileKeyB, _remote));

        public void PrepareExperimentSession(
            string sid,
            ModelProfile profile,
            string systemPrompt,
            bool resetIfExists)
            => PreparedSessions.Add(sid);

        public Task<ExperimentTurnReply> SendExperimentPromptAsync(
            string sid,
            string prompt,
            Func<string>? kvRenewalContextProvider = null)
        {
            var reply = prompt.Contains("GAME: PD or SD", StringComparison.Ordinal)
                ? "GAME: PD\nEXPLANATION: deterministic selection"
                : prompt.StartsWith("ROUND ", StringComparison.Ordinal)
                    ? "c"
                    : "Deterministic explanation.";

            return Task.FromResult(new ExperimentTurnReply(reply, TimeSpan.Zero));
        }

        public void DeleteExperimentSession(string sid)
            => DeletedSessions.Add(sid);

        private ModelProfile Resolve(string? preferredKey, ModelProfile fallback)
            => preferredKey switch
            {
                "local" => _local,
                "remote" => _remote,
                _ => fallback
            };

        private static ModelProfile Profile(string key, string model)
            => new()
            {
                Key = key,
                Model = model,
                Temperature = 0.7,
                TopP = 0.95
            };
    }
}
