using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SocialDilemmaLLMSimulation;

public sealed record RepeatedGameAgentPrompt(
    string Title,
    Func<string, string, string> BuildPromptText)
{
    public string BuildPrompt(string playerName, int rounds)
        => BuildPromptText(playerName, rounds.ToString(CultureInfo.InvariantCulture));

    public string BuildPromptTemplate()
        => BuildPromptText("{PLAYER_NAME}", "{ROUNDS}");
}

public sealed record RepeatedGameRoundRow(
    int Round,
    string MoveA,
    string MoveB,
    int GainA,
    int GainB,
    int CumA,
    int CumB,
    string RawA,
    string RawB);

public sealed record RepeatedGameResult(
    string GameName,
    string SessionA,
    string SessionB,
    int Rounds,
    int FinalScoreA,
    int FinalScoreB,
    List<RepeatedGameRoundRow> Log)
{
    public string Pretty()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{GameName} {Rounds} rounds — {SessionA} vs {SessionB}");
        sb.AppendLine($"Final: {FinalScoreA} - {FinalScoreB}");
        sb.AppendLine("Round | A  B | +A +B | ΣA  ΣB");
        sb.AppendLine("------|------|--------|---------");
        foreach (var row in Log)
            sb.AppendLine($"{row.Round,5} | {row.MoveA}  {row.MoveB} | {row.GainA,2} {row.GainB,2} | {row.CumA,3} {row.CumB,3}");
        return sb.ToString();
    }
}

public abstract class RepeatedGameRunnerBase
{
    protected readonly IRepeatedGameSessionCoordinator _sessionCoordinator;

    protected RepeatedGameRunnerBase(IRepeatedGameSessionCoordinator sessionCoordinator)
    {
        _sessionCoordinator = sessionCoordinator;
    }

    protected abstract RepeatedGameDefinition Definition { get; }
    protected virtual string DefaultRoundPromptVersion => "v1";
    protected virtual string DefaultAgentSystemPromptVersion => "v4";
    protected IReadOnlyDictionary<string, Func<int, int, string?, int, int, string>> RoundPromptCatalog
        => RepeatedGamePromptCatalog.RoundPrompts;
    protected IReadOnlyDictionary<string, RepeatedGameAgentPrompt> AgentPromptCatalog
        => RepeatedGamePromptCatalog.AgentPromptsFor(Definition);
    protected virtual int MaxAgentPromptVersion => 7;

    public string GameCode => Definition.DecisionCode;
    public string GameDisplayName => Definition.PrettyName;

    public IReadOnlyList<string> GetAgentPromptVersions()
        => Enumerable.Range(1, MaxAgentPromptVersion)
            .Select(i => $"v{i}")
            .Where(AgentPromptCatalog.ContainsKey)
            .ToList();

    public RepeatedGameAgentPrompt GetAgentPromptInfo(string version)
    {
        if (!AgentPromptCatalog.TryGetValue(version, out var prompt))
            throw new ArgumentException($"No AgentSystemPrompt defined for '{version}'.", nameof(version));

        return prompt;
    }

    public static string PreviousChoiceExplanationPromptTemplate()
        => BuildPreviousChoiceExplanationPromptText(
            "{ROUND}",
            "{YOUR_MOVE}",
            "{OPPONENT_MOVE}",
            "{YOUR_SCORE_BEFORE}",
            "{OPPONENT_SCORE_BEFORE}",
            "{YOUR_SCORE_AFTER}",
            "{OPPONENT_SCORE_AFTER}");

    public static string PostGameStrategyExplanationPromptTemplate()
        => BuildPostGameStrategyExplanationPromptText(
            "{ROUNDS}",
            "{YOUR_FINAL_SCORE}",
            "{OPPONENT_FINAL_SCORE}",
            "{COOPERATE_COUNT}",
            "{DEFECT_COUNT}");

    public Task<RepeatedGameResult> PlayAsyncSim(
        string sessionA,
        string sessionB,
        int rounds = 50,
        bool resetPrompts = false,
        int runId = 1,
        long? experimentRunId = null)
    {
        return PlayCoreAsync(
            sessionA,
            sessionB,
            rounds,
            resetPrompts,
            DefaultAgentSystemPromptVersion,
            runId,
            experimentRunId: experimentRunId);
    }

    public Task<RepeatedGameResult> PlayAsyncSim(
        string sessionA,
        string sessionB,
        string agentPromptVersion,
        int rounds = 50,
        bool resetPrompts = false,
        int runId = 1,
        long? experimentRunId = null)
    {
        return PlayCoreAsync(
            sessionA,
            sessionB,
            rounds,
            resetPrompts,
            agentPromptVersion,
            runId,
            experimentRunId: experimentRunId);
    }

    public Task<RepeatedGameResult> PlayVersionAsync(
        string sessionA,
        string sessionB,
        string agentPromptVersion,
        int rounds,
        bool resetPrompts,
        int runId,
        string? selectedProfileKeyA = null,
        string? selectedProfileKeyB = null,
        long? experimentRunId = null)
    {
        return PlayCoreAsync(
            sessionA,
            sessionB,
            rounds,
            resetPrompts,
            agentPromptVersion,
            runId,
            selectedProfileKeyA,
            selectedProfileKeyB,
            experimentRunId);
    }

    public async Task<(Dictionary<string, RepeatedGameResult> Results, string RunLabel)> RunV1ToV5SequentialAsync(
        string baseSessionPrefix,
        int rounds = 50,
        bool resetPrompts = true,
        bool clearSessions = true,
        int runId = 1,
        long? experimentRunId = null)
    {
        var results = new Dictionary<string, RepeatedGameResult>();
        var (profileA, profileB) = _sessionCoordinator.ResolveRunModels();
        var runModelLabel = BuildRunLabel(profileA, profileB);
        var effectivePrefix = string.IsNullOrWhiteSpace(baseSessionPrefix)
            ? runModelLabel
            : $"{baseSessionPrefix}__{runModelLabel}";
        var executionTag = CreateExecutionSessionTag();

        var sw = Stopwatch.StartNew();

        for (var i = 1; i <= MaxAgentPromptVersion; i++)
        {
            var version = $"v{i}";
            if (!AgentPromptCatalog.TryGetValue(version, out var agentPrompt))
            {
                Console.WriteLine($"[Skip] {version}: no AgentSystemPrompt defined.");
                continue;
            }

            var sessionPrefix = $"{effectivePrefix}_{Definition.DecisionCode}_exec{executionTag}_run{runId}";
            var sessionA = $"{sessionPrefix}_{version}_A";
            var sessionB = $"{sessionPrefix}_{version}_B";

            Console.WriteLine();
            Console.WriteLine($"===== Running {agentPrompt.Title} ({version}) with {runModelLabel} [run {runId}] =====");
            Console.WriteLine($"Session prefix: {sessionPrefix} (A={sessionA}, B={sessionB})");

            try
            {
                var result = await PlayCoreAsync(
                    sessionA,
                    sessionB,
                    rounds,
                    resetPrompts,
                    version,
                    runId,
                    profileA.Key,
                    profileB.Key,
                    experimentRunId);

                results[version] = result;
                Console.WriteLine(result.Pretty());
            }
            finally
            {
                if (clearSessions)
                    DeleteSessionsQuietly(sessionA, sessionB);
            }
        }

        sw.Stop();
        Console.WriteLine("Elapsed Time (ms): " + sw.ElapsedMilliseconds);
        return (results, runModelLabel);
    }

    public static string BuildRunLabel(string modelA, string modelB)
    {
        return string.Equals(modelA, modelB, StringComparison.OrdinalIgnoreCase)
            ? modelA
            : $"{modelA}_vs_{modelB}";
    }

    public static string BuildRunLabel(ModelProfile profileA, ModelProfile profileB)
    {
        if (!string.Equals(profileA.Model, profileB.Model, StringComparison.OrdinalIgnoreCase))
            return BuildRunLabel(profileA.Model, profileB.Model);

        if (string.Equals(profileA.Key, profileB.Key, StringComparison.OrdinalIgnoreCase))
            return profileA.Model;

        return $"{profileA.Key}_{profileA.Model}_vs_{profileB.Key}_{profileB.Model}";
    }

    internal static string CreateExecutionSessionTag()
        => Guid.NewGuid().ToString("N")[..12];

    private async Task<RepeatedGameResult> PlayCoreAsync(
        string sessionA,
        string sessionB,
        int rounds,
        bool resetPrompts,
        string agentPromptVersion,
        int runId,
        string? selectedProfileKeyA = null,
        string? selectedProfileKeyB = null,
        long? experimentRunId = null)
    {
        var log = new List<RepeatedGameRoundRow>(rounds);
        var scoreA = 0;
        var scoreB = 0;
        var decisions = new List<ContextDecisionWrite>(rounds * 2);
        var explanations = new List<ContextExplanationWrite>((rounds / 10 + 1) * 2);

        var (profileA, profileB) = _sessionCoordinator.ResolveRunModels(
            selectedProfileKeyA,
            selectedProfileKeyB);
        var runModelLabel = BuildRunLabel(profileA, profileB);

        string BuildFullPayoffTableFor(bool isA)
        {
            var sb = new StringBuilder(log.Count * 40 + 128);
            sb.AppendLine("Payoff so far (all rounds)");
            sb.AppendLine("Round | You Opponent | +You +Opponent | ΣYou ΣOpponent");
            sb.AppendLine("------|-------------|--------------|----------------");

            foreach (var row in log)
            {
                if (isA)
                {
                    sb.AppendLine($"{row.Round,5} | {row.MoveA,3} {row.MoveB,8} | {row.GainA,4} {row.GainB,8} | {row.CumA,5} {row.CumB,10}");
                }
                else
                {
                    sb.AppendLine($"{row.Round,5} | {row.MoveB,3} {row.MoveA,8} | {row.GainB,4} {row.GainA,8} | {row.CumB,5} {row.CumA,10}");
                }
            }

            sb.AppendLine(isA
                ? $"Totals: You={scoreA}  Opponent={scoreB}  Rounds={log.Count}"
                : $"Totals: You={scoreB}  Opponent={scoreA}  Rounds={log.Count}");

            return sb.ToString();
        }

        _sessionCoordinator.PrepareExperimentSession(
            sessionA,
            profileA,
            GetAgentSystemPromptString("Player A", rounds, agentPromptVersion),
            resetPrompts);
        _sessionCoordinator.PrepareExperimentSession(
            sessionB,
            profileB,
            GetAgentSystemPromptString("Player B", rounds, agentPromptVersion),
            resetPrompts);

        string? lastA = null;
        string? lastB = null;
        var title = GetAgentSystemPrompt(agentPromptVersion).Title;

        var uniqueName = Util.CreateUniqueName(
            model: runModelLabel,
            game: Definition.UniqueNameCode,
            context: title,
            promptVersion: agentPromptVersion,
            rounds: rounds,
            run_id: runId,
            replicateIndex: 1,
            seed: string.Empty);

        for (var round = 1; round <= rounds; round++)
        {
            var scoreABefore = scoreA;
            var scoreBBefore = scoreB;

            var promptA = RoundPrompt(sessionA, rounds, round, lastB, scoreA, scoreB);
            var promptB = RoundPrompt(sessionB, rounds, round, lastA, scoreB, scoreA);

            var rawA = await _sessionCoordinator.SendExperimentPromptAsync(sessionA, promptA, () => BuildFullPayoffTableFor(isA: true));
            var rawB = await _sessionCoordinator.SendExperimentPromptAsync(sessionB, promptB, () => BuildFullPayoffTableFor(isA: false));

            var moveA = RepeatedGameResponseParser.ParseMove(rawA.Reply)
                ?? throw new InvalidOperationException($"moveA cannot be null. Raw: {rawA.Reply}");
            var moveB = RepeatedGameResponseParser.ParseMove(rawB.Reply)
                ?? throw new InvalidOperationException($"moveB cannot be null. Raw: {rawB.Reply}");

            var (payoffA, payoffB) = Definition.GetPayoff(moveA, moveB);
            var (choiceA, choiceB) = Choice(moveA, moveB);

            scoreA += payoffA;
            scoreB += payoffB;

            log.Add(new RepeatedGameRoundRow(
                round,
                moveA,
                moveB,
                payoffA,
                payoffB,
                scoreA,
                scoreB,
                rawA.Reply.Trim(),
                rawB.Reply.Trim()));

            decisions.Add(new ContextDecisionWrite(
                profileA.Model,
                Definition.DecisionCode,
                title,
                round,
                choiceA,
                scoreA,
                moveA,
                agentPromptVersion,
                runId,
                uniqueName,
                "A",
                uniqueName,
                profileA.Key));
            decisions.Add(new ContextDecisionWrite(
                profileB.Model,
                Definition.DecisionCode,
                title,
                round,
                choiceB,
                scoreB,
                moveB,
                agentPromptVersion,
                runId,
                uniqueName,
                "B",
                uniqueName,
                profileB.Key));

            if (round % 10 == 0)
            {
                try
                {
                    var explainPromptA = BuildPreviousChoiceExplanationPrompt(title, round, moveA, moveB, scoreABefore, scoreBBefore, scoreA, scoreB);
                    var explainPromptB = BuildPreviousChoiceExplanationPrompt(title, round, moveB, moveA, scoreBBefore, scoreABefore, scoreB, scoreA);

                    var explainA = await _sessionCoordinator.SendExperimentPromptAsync(sessionA, explainPromptA, () => BuildFullPayoffTableFor(isA: true));
                    var explainB = await _sessionCoordinator.SendExperimentPromptAsync(sessionB, explainPromptB, () => BuildFullPayoffTableFor(isA: false));

                    explanations.Add(new ContextExplanationWrite(
                        "A",
                        round,
                        "round_10_block",
                        round,
                        explainA.Reply.Trim()));
                    explanations.Add(new ContextExplanationWrite(
                        "B",
                        round,
                        "round_10_block",
                        round,
                        explainB.Reply.Trim()));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Warn] Failed to get round-{round} explanations: {ex.Message}");
                }
            }

            lastA = moveA;
            lastB = moveB;
            Console.WriteLine($"[v={agentPromptVersion}] Round {round} | A: {rawA.Elapsed}  B: {rawB.Elapsed}");
        }

        try
        {
            var postPromptA = BuildPostGameStrategyExplanationPrompt(title, rounds, scoreA, scoreB, log, isA: true);
            var postPromptB = BuildPostGameStrategyExplanationPrompt(title, rounds, scoreB, scoreA, log, isA: false);

            var postA = await _sessionCoordinator.SendExperimentPromptAsync(sessionA, postPromptA, () => BuildFullPayoffTableFor(isA: true));
            var postB = await _sessionCoordinator.SendExperimentPromptAsync(sessionB, postPromptB, () => BuildFullPayoffTableFor(isA: false));

            explanations.Add(new ContextExplanationWrite(
                "A",
                rounds,
                "post_game",
                null,
                postA.Reply.Trim()));
            explanations.Add(new ContextExplanationWrite(
                "B",
                rounds,
                "post_game",
                null,
                postB.Reply.Trim()));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Warn] Failed to get post-game explanations: {ex.Message}");
        }

        ContextRunLogger.InsertContextRun(experimentRunId, decisions, explanations);
        return new RepeatedGameResult(Definition.PrettyName, sessionA, sessionB, rounds, scoreA, scoreB, log);
    }

    private void DeleteSessionsQuietly(string sessionA, string sessionB)
    {
        try
        {
            _sessionCoordinator.DeleteExperimentSession(sessionA);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Warn] Failed to clear {sessionA}: {ex.Message}");
        }

        try
        {
            _sessionCoordinator.DeleteExperimentSession(sessionB);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Warn] Failed to clear {sessionB}: {ex.Message}");
        }
    }

    private string GetRoundPromptString(
        int totalRounds,
        int round,
        string? lastOpponentMove,
        int myScore,
        int oppScore,
        string? version = null)
    {
        var key = version ?? DefaultRoundPromptVersion;
        if (!RoundPromptCatalog.TryGetValue(key, out var prompt))
            prompt = RoundPromptCatalog[DefaultRoundPromptVersion];
        return prompt(totalRounds, round, lastOpponentMove, myScore, oppScore);
    }

    private string GetAgentSystemPromptString(string name, int rounds, string? version = null)
    {
        var key = version ?? DefaultAgentSystemPromptVersion;
        if (!AgentPromptCatalog.TryGetValue(key, out var prompt))
            prompt = AgentPromptCatalog[DefaultAgentSystemPromptVersion];
        return prompt.BuildPrompt(name, rounds);
    }

    private RepeatedGameAgentPrompt GetAgentSystemPrompt(string? version = null)
    {
        var key = version ?? DefaultAgentSystemPromptVersion;
        if (!AgentPromptCatalog.TryGetValue(key, out var prompt))
            prompt = AgentPromptCatalog[DefaultAgentSystemPromptVersion];
        return prompt;
    }

    private string RoundPrompt(
        string me,
        int totalRounds,
        int round,
        string? lastOpponentMove,
        int myScore,
        int oppScore)
    {
        Console.WriteLine($"[Round {round}] Player: {me} | My Score: {myScore}, Opponent Score: {oppScore} | Last Opponent Move: {(lastOpponentMove ?? "BLIND")}");
        return GetRoundPromptString(
            totalRounds,
            round,
            lastOpponentMove,
            myScore,
            oppScore,
            DefaultRoundPromptVersion);
    }

    private static string BuildPreviousChoiceExplanationPrompt(
        string scenarioTitle,
        int round,
        string myMove,
        string opponentMove,
        int myScoreBefore,
        int oppScoreBefore,
        int myScoreAfter,
        int oppScoreAfter)
    {
        _ = scenarioTitle;

        var myLabel = myMove switch
        {
            "c" or "C" => "cooperate ('c')",
            "d" or "D" => "defect ('d')",
            _ => $"'{myMove}'"
        };

        var oppLabel = opponentMove switch
        {
            "c" or "C" => "cooperate ('c')",
            "d" or "D" => "defect ('d')",
            _ => $"'{opponentMove}'"
        };

        return BuildPreviousChoiceExplanationPromptText(
            round.ToString(),
            myLabel,
            oppLabel,
            myScoreBefore.ToString(),
            oppScoreBefore.ToString(),
            myScoreAfter.ToString(),
            oppScoreAfter.ToString());
    }

    private static string BuildPreviousChoiceExplanationPromptText(
        string round,
        string myMove,
        string opponentMove,
        string myScoreBefore,
        string oppScoreBefore,
        string myScoreAfter,
        string oppScoreAfter)
    {
        return $"""
You have just completed round {round}.
In that round, you chose {myMove}, and the other side chose {opponentMove}.

Before this round, the cumulative scores were:
- you: {myScoreBefore}
- other side: {oppScoreBefore}

After this round, the cumulative scores are:
- you: {myScoreAfter}
- other side: {oppScoreAfter}

In 3-6 sentences, describe what led you to that choice in this round:
- what you inferred from earlier rounds,
- how you interpreted the other side's behaviour,
- and how this decision fits into your overall approach across rounds.
- did the context affect your behavior, if it did, how?

Answer in natural language only. Do not respond with just 'c' or 'd'.
""".Trim();
    }

    private static string BuildPostGameStrategyExplanationPrompt(
        string scenarioTitle,
        int rounds,
        int myFinalScore,
        int oppFinalScore,
        List<RepeatedGameRoundRow> log,
        bool isA)
    {
        _ = scenarioTitle;

        var cCount = 0;
        var dCount = 0;
        foreach (var row in log)
        {
            var move = isA ? row.MoveA : row.MoveB;
            if (move is "c" or "C") cCount++;
            else if (move is "d" or "D") dCount++;
        }

        return BuildPostGameStrategyExplanationPromptText(
            rounds.ToString(),
            myFinalScore.ToString(),
            oppFinalScore.ToString(),
            cCount.ToString(),
            dCount.ToString());
    }

    private static string BuildPostGameStrategyExplanationPromptText(
        string rounds,
        string myFinalScore,
        string oppFinalScore,
        string cCount,
        string dCount)
    {
        return $"""
You have completed {rounds} repeated rounds with the same other side.
Your final cumulative score is {myFinalScore}; the other side's final score is {oppFinalScore}.
Across these {rounds} rounds, you chose:
- 'c' in {cCount} rounds
- 'd' in {dCount} rounds.

In 5-10 sentences, explain your overall approach during these rounds:
- how you decided to start out,
- how you reacted to the other side's behaviour over time,
- whether you aimed for stable cooperation, punishment of defection, consistent defection, or something else,
- and how you now evaluate the outcome and your choices in hindsight.

Respond in natural language; do not answer with just 'c' or 'd'.
""".Trim();
    }

    private static (int a, int b) Choice(string a, string b)
    {
        return (a, b) switch
        {
            ("c", "c") => (1, 1),
            ("c", "d") => (1, 0),
            ("d", "c") => (0, 1),
            ("d", "d") => (0, 0),
            _ => throw new ArgumentOutOfRangeException($"Invalid moves: a='{a}', b='{b}'. Allowed values: c/d (or A/B aliases).")
        };
    }

}
