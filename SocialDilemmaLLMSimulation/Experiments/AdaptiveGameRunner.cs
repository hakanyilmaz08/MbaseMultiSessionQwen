using System.Text;

namespace SocialDilemmaLLMSimulation;

public sealed record AdaptiveGameContextRun(
    int RunId,
    string PromptVersion,
    string ContextTitle,
    string GameCode,
    RepeatedGameResult Result);

public sealed record AdaptiveSelectionRow(
    int RunId,
    string ChoiceA,
    string ChoiceB,
    string ResolvedGame,
    int? RandomRoll,
    string RawA,
    string RawB);

public sealed record AdaptiveGameResult(
    string RunLabel,
    List<AdaptiveGameContextRun> GameRuns,
    List<AdaptiveSelectionRow> Selections)
{
    public string Pretty()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Adaptive game run: {RunLabel}");
        sb.AppendLine($"Completed repeated games: {GameRuns.Count}");
        sb.AppendLine($"Game-selection decisions: {Selections.Count}");
        sb.AppendLine();
        sb.AppendLine("Run | Context | Game | Final A | Final B");
        sb.AppendLine("----|---------|------|---------|--------");

        foreach (var gameRun in GameRuns.OrderBy(r => r.RunId).ThenBy(r => r.PromptVersion))
        {
            sb.AppendLine(
                $"{gameRun.RunId,3} | {gameRun.ContextTitle} ({gameRun.PromptVersion}) | {gameRun.GameCode} | {gameRun.Result.FinalScoreA,7} | {gameRun.Result.FinalScoreB,7}");
        }

        if (Selections.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Selection Run | A | B | Resolved | Roll");
            sb.AppendLine("--------------|---|---|----------|-----");
            foreach (var selection in Selections.OrderBy(s => s.RunId))
            {
                sb.AppendLine(
                    $"{selection.RunId,13} | {selection.ChoiceA} | {selection.ChoiceB} | {selection.ResolvedGame} | {(selection.RandomRoll?.ToString() ?? "-")}");
            }
        }

        return sb.ToString();
    }
}

public sealed class AdaptiveGameRunner
{
    private const int TotalRuns = 20;
    private const int FirstAdaptiveRun = 3;
    private readonly IRepeatedGameSessionCoordinator _sessionCoordinator;
    private readonly RepeatedGameRunnerBase _pdRunner;
    private readonly RepeatedGameRunnerBase _sdRunner;
    private readonly Random? _seededRandom;

    public AdaptiveGameRunner(IRepeatedGameSessionCoordinator sessionCoordinator)
    {
        _sessionCoordinator = sessionCoordinator;
        _pdRunner = new IPDRunner(sessionCoordinator);
        _sdRunner = new ISDRunner(sessionCoordinator);

        if (int.TryParse(Util.DetectEnv("ADAPTIVE_RANDOM_SEED", ""), out var seed))
            _seededRandom = new Random(seed);
    }

    public async Task<AdaptiveGameResult> RunAsync(string baseSessionPrefix = "adaptive", int rounds = 50)
    {
        var (modelA, modelB) = _sessionCoordinator.ResolveRunModels();
        var runLabel = RepeatedGameRunnerBase.BuildRunLabel(modelA, modelB);
        var sessionPrefix = string.IsNullOrWhiteSpace(baseSessionPrefix)
            ? runLabel
            : $"{baseSessionPrefix}__{runLabel}";

        var versions = _pdRunner.GetAgentPromptVersions()
            .Intersect(_sdRunner.GetAgentPromptVersions(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var gameRuns = new List<AdaptiveGameContextRun>(TotalRuns * versions.Count);
        var selections = new List<AdaptiveSelectionRow>(TotalRuns - 2);

        foreach (var version in versions)
            gameRuns.Add(await PlayContextGameAsync(_pdRunner, sessionPrefix, version, runId: 1, rounds, modelA, modelB));

        foreach (var version in versions)
            gameRuns.Add(await PlayContextGameAsync(_sdRunner, sessionPrefix, version, runId: 2, rounds, modelA, modelB));

        for (var runId = FirstAdaptiveRun; runId <= TotalRuns; runId++)
        {
            var feedbackSnapshot = gameRuns.ToList();
            var selection = await SelectNextGameAsync(
                sessionPrefix,
                runId,
                feedbackSnapshot,
                modelA,
                modelB);

            selections.Add(selection);
            var runner = selection.ResolvedGame == "PD" ? _pdRunner : _sdRunner;

            foreach (var version in versions)
            {
                gameRuns.Add(await PlayContextGameAsync(runner, sessionPrefix, version, runId, rounds, modelA, modelB));
            }
        }

        return new AdaptiveGameResult(runLabel, gameRuns, selections);
    }

    private async Task<AdaptiveGameContextRun> PlayContextGameAsync(
        RepeatedGameRunnerBase runner,
        string sessionPrefix,
        string version,
        int runId,
        int rounds,
        string modelA,
        string modelB)
    {
        var contextTitle = runner.GetAgentPromptInfo(version).Title;
        var sessionBase = $"{sessionPrefix}_run{runId}_{runner.GameCode}_{version}";
        var sessionA = $"{sessionBase}_A";
        var sessionB = $"{sessionBase}_B";

        Console.WriteLine();
        Console.WriteLine($"===== Adaptive run {runId}: {runner.GameDisplayName} {contextTitle} ({version}) =====");

        try
        {
            var result = await runner.PlayVersionAsync(
                sessionA,
                sessionB,
                version,
                rounds,
                resetPrompts: true,
                runId,
                modelA,
                modelB);

            return new AdaptiveGameContextRun(runId, version, contextTitle, runner.GameCode, result);
        }
        finally
        {
            DeleteSessionQuietly(sessionA);
            DeleteSessionQuietly(sessionB);
        }
    }

    private async Task<AdaptiveSelectionRow> SelectNextGameAsync(
        string sessionPrefix,
        int runId,
        IReadOnlyList<AdaptiveGameContextRun> feedbackSnapshot,
        string modelA,
        string modelB)
    {
        const string selectionContext = "All Contexts";
        const string selectionPromptVersion = "all";
        var selectionBase = $"{sessionPrefix}_select_run{runId}";
        var sessionA = $"{selectionBase}_A";
        var sessionB = $"{selectionBase}_B";
        var systemPrompt = BuildSelectionSystemPrompt();
        var promptA = BuildSelectionPrompt("A", feedbackSnapshot);
        var promptB = BuildSelectionPrompt("B", feedbackSnapshot);

        try
        {
            _sessionCoordinator.PrepareExperimentSession(sessionA, modelA, systemPrompt, resetIfExists: true);
            _sessionCoordinator.PrepareExperimentSession(sessionB, modelB, systemPrompt, resetIfExists: true);

            var rawA = await _sessionCoordinator.SendExperimentPromptAsync(sessionA, promptA);
            var rawB = await _sessionCoordinator.SendExperimentPromptAsync(sessionB, promptB);

            var choiceA = ParseGameChoice(rawA.Reply)
                ?? throw new InvalidOperationException($"Game selection for player A could not be parsed. Raw: {rawA.Reply}");
            var choiceB = ParseGameChoice(rawB.Reply)
                ?? throw new InvalidOperationException($"Game selection for player B could not be parsed. Raw: {rawB.Reply}");

            int? randomRoll = string.Equals(choiceA, choiceB, StringComparison.OrdinalIgnoreCase)
                ? null
                : NextRoll();
            var resolvedGame = randomRoll is null
                ? choiceA
                : randomRoll > 50 ? "PD" : "SD";

            var uniqueName = BuildSelectionUniqueName(sessionPrefix, runId);
            GameSelectionLogger.InsertSelection(
                runId,
                uniqueName,
                modelA,
                selectionContext,
                selectionPromptVersion,
                "A",
                choiceA,
                resolvedGame,
                randomRoll,
                rawA.Reply.Trim(),
                ExtractExplanation(rawA.Reply));
            GameSelectionLogger.InsertSelection(
                runId,
                uniqueName,
                modelB,
                selectionContext,
                selectionPromptVersion,
                "B",
                choiceB,
                resolvedGame,
                randomRoll,
                rawB.Reply.Trim(),
                ExtractExplanation(rawB.Reply));

            Console.WriteLine(
                $"[Adaptive selection] run={runId} A={choiceA} B={choiceB} resolved={resolvedGame} roll={(randomRoll?.ToString() ?? "-")}");

            return new AdaptiveSelectionRow(
                runId,
                choiceA,
                choiceB,
                resolvedGame,
                randomRoll,
                rawA.Reply.Trim(),
                rawB.Reply.Trim());
        }
        finally
        {
            DeleteSessionQuietly(sessionA);
            DeleteSessionQuietly(sessionB);
        }
    }

    private static string BuildSelectionSystemPrompt()
        => """
You are choosing the next repeated game type for all seven contexts in the next run.
You must choose exactly one game type: PD or SD.
Base your choice only on the feedback summary in the user message.
""".Trim();

    public static string SelectionSystemPromptTemplate()
        => BuildSelectionSystemPrompt();

    public static string SelectionUserPromptTemplate()
        => """
You are Player {PLAYER_ROLE}.
Choose which game type you want to play for the next run across all seven contexts: PD or SD.

Previous payoff history and decision-state counts across all completed runs and contexts:
Decision-state payoff values by game type from your perspective:
Game | R score | S score | T score | P score
-----|---------|---------|---------|--------
PD | 5 | 0 | 10 | 1
SD | 5 | 1 | 10 | 0

Run | Context | Game | You | Opponent | R count | S count | T count | P count
----|---------|------|-----|----------|---------|---------|---------|--------
{COMPLETED_RUN_FEEDBACK_ROWS}

Decision-state definitions from your perspective:
R: you chose c and the opponent chose c.
S: you chose c and the opponent chose d.
T: you chose d and the opponent chose c.
P: you chose d and the opponent chose d.

Respond in this exact format:
GAME: PD or SD
EXPLANATION: 3-6 sentences explaining why you chose that game type.
""".Trim();

    private static string BuildSelectionPrompt(
        string playerRole,
        IReadOnlyList<AdaptiveGameContextRun> completedRuns)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are Player {playerRole}.");
        sb.AppendLine("Choose which game type you want to play for the next run across all seven contexts: PD or SD.");
        sb.AppendLine();
        sb.AppendLine("Previous payoff history and decision-state counts across all completed runs and contexts:");
        sb.AppendLine("Decision-state payoff values by game type from your perspective:");
        sb.AppendLine("Game | R score | S score | T score | P score");
        sb.AppendLine("-----|---------|---------|---------|--------");
        sb.AppendLine("PD | 5 | 0 | 10 | 1");
        sb.AppendLine("SD | 5 | 1 | 10 | 0");
        sb.AppendLine();
        sb.AppendLine("Run | Context | Game | You | Opponent | R count | S count | T count | P count");
        sb.AppendLine("----|---------|------|-----|----------|---------|---------|---------|--------");

        foreach (var run in completedRuns.OrderBy(r => r.RunId).ThenBy(r => r.PromptVersion))
        {
            var summary = PlayerStateSummary.From(run.Result, playerRole);
            sb.AppendLine(
                $"{run.RunId} | {run.ContextTitle} ({run.PromptVersion}) | {run.GameCode} | {summary.Score} | {summary.OpponentScore} | {summary.R} | {summary.S} | {summary.T} | {summary.P}");
        }

        sb.AppendLine();
        sb.AppendLine("Decision-state definitions from your perspective:");
        sb.AppendLine("R: you chose c and the opponent chose c.");
        sb.AppendLine("S: you chose c and the opponent chose d.");
        sb.AppendLine("T: you chose d and the opponent chose c.");
        sb.AppendLine("P: you chose d and the opponent chose d.");
        sb.AppendLine();
        sb.AppendLine("Respond in this exact format:");
        sb.AppendLine("GAME: PD or SD");
        sb.AppendLine("EXPLANATION: 3-6 sentences explaining why you chose that game type.");
        return sb.ToString().Trim();
    }

    private int NextRoll()
        => (_seededRandom ?? Random.Shared).Next(1, 101);

    private void DeleteSessionQuietly(string sessionId)
    {
        try
        {
            _sessionCoordinator.DeleteExperimentSession(sessionId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Warn] Failed to clear {sessionId}: {ex.Message}");
        }
    }

    private static string? ParseGameChoice(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var normalized = raw.Trim();
        if (StartsWithChoice(normalized, "PD"))
            return "PD";
        if (StartsWithChoice(normalized, "SD"))
            return "SD";

        var tokens = normalized
            .Split(new[] { ' ', '\r', '\n', '\t', ':', ';', ',', '.', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in tokens)
        {
            if (string.Equals(token, "PD", StringComparison.OrdinalIgnoreCase))
                return "PD";
            if (string.Equals(token, "SD", StringComparison.OrdinalIgnoreCase))
                return "SD";
        }

        return null;
    }

    private static bool StartsWithChoice(string value, string choice)
        => value.StartsWith(choice, StringComparison.OrdinalIgnoreCase)
            || value.StartsWith($"GAME: {choice}", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith($"GAME {choice}", StringComparison.OrdinalIgnoreCase);

    private static string ExtractExplanation(string raw)
    {
        var marker = "EXPLANATION:";
        var index = raw.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return raw.Trim();

        return raw[(index + marker.Length)..].Trim();
    }

    private static string BuildSelectionUniqueName(string sessionPrefix, int runId)
        => string.Join("__", new[]
        {
            Sanitize(sessionPrefix),
            "adaptive_selection",
            $"run{runId}"
        });

    private static string Sanitize(string value)
        => new string(value.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    private sealed record PlayerStateSummary(int Score, int OpponentScore, int R, int T, int S, int P)
    {
        public static PlayerStateSummary From(RepeatedGameResult result, string playerRole)
        {
            var isA = string.Equals(playerRole, "A", StringComparison.OrdinalIgnoreCase);
            var r = 0;
            var t = 0;
            var s = 0;
            var p = 0;

            foreach (var row in result.Log)
            {
                var mine = isA ? row.MoveA : row.MoveB;
                var theirs = isA ? row.MoveB : row.MoveA;

                if (mine == "c" && theirs == "c") r++;
                else if (mine == "d" && theirs == "c") t++;
                else if (mine == "c" && theirs == "d") s++;
                else if (mine == "d" && theirs == "d") p++;
            }

            return isA
                ? new PlayerStateSummary(result.FinalScoreA, result.FinalScoreB, r, t, s, p)
                : new PlayerStateSummary(result.FinalScoreB, result.FinalScoreA, r, t, s, p);
        }
    }
}
