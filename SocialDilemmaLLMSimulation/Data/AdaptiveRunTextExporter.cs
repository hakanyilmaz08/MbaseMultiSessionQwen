using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using Microsoft.Data.Sqlite;
using SocialDilemmaLLMSimulation;

public sealed record AdaptiveRunTextExportResult(
    string OutputFolder,
    int GameSelectionDecisionCount,
    int DecisionExplanationCount,
    int ContextRunSummaryCount);

public static class AdaptiveRunTextExporter
{
    public static AdaptiveRunTextExportResult ExportLastPlayAdaptive()
    {
        using var connection = new SqliteConnection(ExperimentPaths.DatabaseConnectionString);
        connection.Open();

        var adaptiveRun = AdaptiveRunExportRepository.LoadLastCompletedAdaptiveRun(connection)
            ?? AdaptiveRunExportRepository.LoadLatestLegacyAdaptiveRun(connection)
            ?? throw new InvalidOperationException("No completed /playadaptive run was found in the database.");

        var gameSelections = AdaptiveRunExportRepository.LoadGameSelectionRows(connection, adaptiveRun);
        var explanations = AdaptiveRunExportRepository.LoadDecisionExplanationRows(connection, adaptiveRun);
        var decisions = AdaptiveRunExportRepository.LoadAdaptiveDecisionRows(connection, adaptiveRun);
        var contextRunSummaries = AdaptiveRunExportProjector.BuildContextRunSummaries(decisions);

        var firstPromptTime = gameSelections.Count > 0
            ? AdaptiveRunExportRepository.ParseTimestamp(gameSelections[0].Timestamp)
            : adaptiveRun.StartedAt;

        var folderName = $"adaptive_{firstPromptTime.ToLocalTime():yyyy-MM-dd_HH-mm}";
        var outputFolder = ExperimentPaths.EnsureDirectory(Path.Combine(ExperimentPaths.EnsureExportsDirectory(), folderName));

        File.WriteAllText(
            Path.Combine(outputFolder, "game-selection-decisions.txt"),
            AdaptiveRunExportFormatter.BuildGameSelectionText(adaptiveRun.RunLabel, gameSelections),
            Encoding.UTF8);

        File.WriteAllText(
            Path.Combine(outputFolder, "decision-explanations.txt"),
            AdaptiveRunExportFormatter.BuildDecisionExplanationsText(adaptiveRun.RunLabel, explanations),
            Encoding.UTF8);

        AdaptiveRunExportFormatter.WriteContextRunSummaryWorkbook(
            Path.Combine(outputFolder, "context-run-payoff-summary.xlsx"),
            adaptiveRun.RunLabel,
            contextRunSummaries);

        File.WriteAllText(
            Path.Combine(outputFolder, "agent-explanation-prompt-templates.txt"),
            AdaptiveRunExportFormatter.BuildPromptTemplateText(),
            Encoding.UTF8);

        return new AdaptiveRunTextExportResult(outputFolder, gameSelections.Count, explanations.Count, contextRunSummaries.Count);
    }
}

internal static class AdaptiveRunExportRepository
{
    private const int FirstAdaptiveRunId = 3;
    private const string AdaptiveSelectionMarker = "__adaptive_selection__run";

    internal static AdaptiveRunRow? LoadLastCompletedAdaptiveRun(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.id,
                   a.run_label,
                   a.started_at,
                   a.completed_at,
                   CASE
                       WHEN EXISTS (
                           SELECT 1
                           FROM decisions d
                           WHERE d.experiment_run_id = a.id
                       )
                       OR EXISTS (
                           SELECT 1
                           FROM game_selection_decisions s
                           WHERE s.experiment_run_id = a.id
                       )
                       THEN a.id
                       ELSE NULL
                   END AS experiment_run_id
            FROM adaptive_runs a
            WHERE a.status = 'completed'
              AND a.completed_at IS NOT NULL
            ORDER BY a.completed_at DESC, a.id DESC
            LIMIT 1;
            """;

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return new AdaptiveRunRow(
            Id: reader.GetInt64(0),
            RunLabel: reader.GetString(1),
            StartedAt: ParseTimestamp(reader.GetString(2)),
            CompletedAt: ParseTimestamp(reader.GetString(3)),
            ExperimentRunId: reader.IsDBNull(4) ? null : reader.GetInt64(4));
    }

    internal static AdaptiveRunRow? LoadLatestLegacyAdaptiveRun(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, unique_name
            FROM game_selection_decisions
            WHERE unique_name LIKE $marker
            ORDER BY id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$marker", $"%{AdaptiveSelectionMarker}%");

        long latestId;
        string latestUniqueName;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
                return null;

            latestId = reader.GetInt64(0);
            latestUniqueName = reader.GetString(1);
        }

        if (string.IsNullOrWhiteSpace(latestUniqueName))
            return null;

        var markerIndex = latestUniqueName.IndexOf(AdaptiveSelectionMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex <= 0)
            return null;

        var prefix = latestUniqueName[..markerIndex];
        var selections = LoadLatestLegacySelectionSegment(connection, latestId, prefix);
        if (selections.Count == 0)
            return null;

        var firstSelectionId = selections[0].Id;
        var lastSelectionId = selections[^1].Id;
        var previousSelectionTimestamp = LoadPreviousGameSelectionTimestamp(connection, firstSelectionId);
        var runLabel = prefix.StartsWith("adaptive__", StringComparison.OrdinalIgnoreCase)
            ? prefix["adaptive__".Length..]
            : prefix;

        return new AdaptiveRunRow(
            Id: 0,
            RunLabel: runLabel,
            StartedAt: previousSelectionTimestamp ?? DateTimeOffset.MinValue,
            CompletedAt: DateTimeOffset.Now,
            LegacySelectionFirstId: firstSelectionId,
            LegacySelectionLastId: lastSelectionId,
            LegacyDecisionUniqueNamePrefix: $"{runLabel}__",
            IsLegacyInference: true);
    }

    internal static List<GameSelectionRow> LoadGameSelectionRows(SqliteConnection connection, AdaptiveRunRow adaptiveRun)
    {
        if (adaptiveRun.LegacySelectionFirstId is not null && adaptiveRun.LegacySelectionLastId is not null)
            return LoadGameSelectionRowsForIdRange(
                connection,
                adaptiveRun.LegacySelectionFirstId.Value,
                adaptiveRun.LegacySelectionLastId.Value);

        using var command = connection.CreateCommand();
        if (adaptiveRun.ExperimentRunId is not null)
        {
            command.CommandText = """
                SELECT id, run_id, unique_name, model_profile_key, model, context, prompt_version, player_role,
                       selected_game, resolved_game, random_roll, raw_response, explanation, timestamp
                FROM game_selection_decisions
                WHERE experiment_run_id = $experiment_run_id
                ORDER BY id;
                """;
            command.Parameters.AddWithValue("$experiment_run_id", adaptiveRun.ExperimentRunId.Value);
            return ReadGameSelectionRows(command);
        }

        command.CommandText = """
            SELECT id, run_id, unique_name, model_profile_key, model, context, prompt_version, player_role,
                   selected_game, resolved_game, random_roll, raw_response, explanation, timestamp
            FROM game_selection_decisions
            WHERE timestamp >= $started_at
              AND timestamp <= $completed_at
            ORDER BY id;
            """;
        command.Parameters.AddWithValue("$started_at", FormatSqliteTimestamp(adaptiveRun.StartedAt));
        command.Parameters.AddWithValue("$completed_at", FormatSqliteTimestamp(adaptiveRun.CompletedAt));

        return ReadGameSelectionRows(command);
    }

    private static List<GameSelectionRow> LoadLatestLegacySelectionSegment(
        SqliteConnection connection,
        long latestId,
        string selectionPrefix)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, run_id, unique_name, model_profile_key, model, context, prompt_version, player_role,
                   selected_game, resolved_game, random_roll, raw_response, explanation, timestamp
            FROM game_selection_decisions
            WHERE id <= $latest_id
              AND substr(unique_name, 1, length($selection_prefix)) = $selection_prefix
              AND unique_name LIKE $marker
            ORDER BY id DESC
            LIMIT 200;
            """;
        command.Parameters.AddWithValue("$latest_id", latestId);
        command.Parameters.AddWithValue("$selection_prefix", selectionPrefix);
        command.Parameters.AddWithValue("$marker", $"%{AdaptiveSelectionMarker}%");

        var descendingRows = ReadGameSelectionRows(command);
        var segment = new List<GameSelectionRow>();
        var firstRunRowsSeen = 0;
        int? previousRunId = null;

        foreach (var row in descendingRows)
        {
            if (previousRunId is not null && row.RunId > previousRunId.Value)
                break;

            segment.Add(row);
            previousRunId = row.RunId;

            if (row.RunId == FirstAdaptiveRunId)
            {
                firstRunRowsSeen++;
                if (firstRunRowsSeen >= 2)
                    break;
            }
        }

        segment.Reverse();
        return segment;
    }

    private static DateTimeOffset? LoadPreviousGameSelectionTimestamp(SqliteConnection connection, long firstSelectionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT timestamp
            FROM game_selection_decisions
            WHERE id < $first_selection_id
            ORDER BY id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$first_selection_id", firstSelectionId);

        var value = command.ExecuteScalar();
        return value is string timestamp && !string.IsNullOrWhiteSpace(timestamp)
            ? ParseTimestamp(timestamp)
            : null;
    }

    private static List<GameSelectionRow> LoadGameSelectionRowsForIdRange(
        SqliteConnection connection,
        long firstSelectionId,
        long lastSelectionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, run_id, unique_name, model_profile_key, model, context, prompt_version, player_role,
                   selected_game, resolved_game, random_roll, raw_response, explanation, timestamp
            FROM game_selection_decisions
            WHERE id >= $first_selection_id
              AND id <= $last_selection_id
            ORDER BY id;
            """;
        command.Parameters.AddWithValue("$first_selection_id", firstSelectionId);
        command.Parameters.AddWithValue("$last_selection_id", lastSelectionId);

        return ReadGameSelectionRows(command);
    }

    private static List<GameSelectionRow> ReadGameSelectionRows(SqliteCommand command)
    {
        var rows = new List<GameSelectionRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new GameSelectionRow(
                Id: reader.GetInt64(0),
                RunId: reader.GetInt32(1),
                UniqueName: reader.GetString(2),
                ModelProfileKey: reader.IsDBNull(3) ? "" : reader.GetString(3),
                Model: reader.GetString(4),
                Context: reader.GetString(5),
                PromptVersion: reader.GetString(6),
                PlayerRole: reader.GetString(7),
                SelectedGame: reader.GetString(8),
                ResolvedGame: reader.GetString(9),
                RandomRoll: reader.IsDBNull(10) ? null : reader.GetInt32(10),
                RawResponse: reader.GetString(11),
                Explanation: reader.GetString(12),
                Timestamp: reader.GetString(13)));
        }

        return rows;
    }

    internal static List<DecisionExplanationRow> LoadDecisionExplanationRows(SqliteConnection connection, AdaptiveRunRow adaptiveRun)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.id,
                   e.decision_id,
                   e.explanation_type,
                   e.round,
                   e.explanation,
                   e.timestamp,
                   d.run_id,
                   d.unique_name,
                   d.model_profile_key,
                   d.model,
                   d.game,
                   d.context,
                   d.prompt_version,
                   d.player_role,
                   d.round,
                   d.choice,
                   d.payoff,
                   d.raw_response,
                   d.timestamp
            FROM decision_explanations e
            INNER JOIN decisions d ON d.id = e.decision_id
            WHERE d.experiment_run_id = $experiment_run_id
            ORDER BY e.id;
            """;
        if (adaptiveRun.ExperimentRunId is not null)
        {
            command.Parameters.AddWithValue("$experiment_run_id", adaptiveRun.ExperimentRunId.Value);
        }
        else
        {
            command.CommandText = command.CommandText.Replace(
                "WHERE d.experiment_run_id = $experiment_run_id",
                """
                WHERE e.timestamp >= $started_at
                  AND e.timestamp <= $completed_at
                """,
                StringComparison.Ordinal);
            command.Parameters.AddWithValue("$started_at", FormatSqliteTimestamp(adaptiveRun.StartedAt));
            command.Parameters.AddWithValue("$completed_at", FormatSqliteTimestamp(adaptiveRun.CompletedAt));

            if (!string.IsNullOrWhiteSpace(adaptiveRun.LegacyDecisionUniqueNamePrefix))
            {
                command.CommandText = command.CommandText.Replace(
                    "WHERE e.timestamp >= $started_at",
                    "WHERE substr(d.unique_name, 1, length($decision_unique_name_prefix)) = $decision_unique_name_prefix\n                  AND e.timestamp >= $started_at",
                    StringComparison.Ordinal);
                command.Parameters.AddWithValue(
                    "$decision_unique_name_prefix",
                    adaptiveRun.LegacyDecisionUniqueNamePrefix);
            }
        }

        var rows = new List<DecisionExplanationRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new DecisionExplanationRow(
                Id: reader.GetInt64(0),
                DecisionId: reader.GetInt64(1),
                ExplanationType: reader.GetString(2),
                ExplanationRound: reader.IsDBNull(3) ? null : reader.GetInt32(3),
                Explanation: reader.GetString(4),
                ExplanationTimestamp: reader.GetString(5),
                RunId: reader.GetInt32(6),
                UniqueName: reader.GetString(7),
                ModelProfileKey: reader.IsDBNull(8) ? "" : reader.GetString(8),
                Model: reader.GetString(9),
                Game: reader.GetString(10),
                Context: reader.GetString(11),
                PromptVersion: reader.GetString(12),
                PlayerRole: reader.IsDBNull(13) ? "" : reader.GetString(13),
                DecisionRound: reader.GetInt32(14),
                Choice: reader.GetInt32(15),
                Payoff: reader.GetInt32(16),
                RawResponse: reader.GetString(17),
                DecisionTimestamp: reader.GetString(18)));
        }

        return rows;
    }

    internal static List<GameDecisionRow> LoadAdaptiveDecisionRows(SqliteConnection connection, AdaptiveRunRow adaptiveRun)
    {
        if (adaptiveRun.ExperimentRunId is not null)
        {
            using var scopedCommand = connection.CreateCommand();
            scopedCommand.CommandText = """
                SELECT id, run_id, unique_name, model_profile_key, model, game, context, prompt_version,
                       player_role, round, choice, payoff, raw_response, timestamp
                FROM decisions
                WHERE experiment_run_id = $experiment_run_id
                ORDER BY run_id, context, prompt_version, game, unique_name, round, player_role, id;
                """;
            scopedCommand.Parameters.AddWithValue(
                "$experiment_run_id",
                adaptiveRun.ExperimentRunId.Value);
            return ReadGameDecisionRows(scopedCommand);
        }

        var uniqueNames = LoadAdaptiveDecisionUniqueNames(connection, adaptiveRun);
        if (uniqueNames.Count == 0)
            return new List<GameDecisionRow>();

        using var command = connection.CreateCommand();
        var parameters = new List<string>(uniqueNames.Count);
        for (var i = 0; i < uniqueNames.Count; i++)
        {
            var parameterName = $"$unique_name_{i}";
            parameters.Add(parameterName);
            command.Parameters.AddWithValue(parameterName, uniqueNames[i]);
        }

        command.CommandText = $"""
            SELECT id, run_id, unique_name, model_profile_key, model, game, context, prompt_version,
                   player_role, round, choice, payoff, raw_response, timestamp
            FROM decisions
            WHERE unique_name IN ({string.Join(", ", parameters)})
            ORDER BY run_id, context, prompt_version, game, unique_name, round, player_role, id;
            """;

        return ReadGameDecisionRows(command);
    }

    private static List<GameDecisionRow> ReadGameDecisionRows(SqliteCommand command)
    {
        var rows = new List<GameDecisionRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new GameDecisionRow(
                Id: reader.GetInt64(0),
                RunId: reader.GetInt32(1),
                UniqueName: reader.GetString(2),
                ModelProfileKey: reader.IsDBNull(3) ? "" : reader.GetString(3),
                Model: reader.GetString(4),
                Game: reader.GetString(5),
                Context: reader.GetString(6),
                PromptVersion: reader.GetString(7),
                PlayerRole: reader.IsDBNull(8) ? "" : reader.GetString(8),
                Round: reader.GetInt32(9),
                Choice: reader.GetInt32(10),
                Payoff: reader.GetInt32(11),
                RawResponse: reader.GetString(12),
                Timestamp: reader.GetString(13)));
        }

        return rows;
    }

    private static List<string> LoadAdaptiveDecisionUniqueNames(SqliteConnection connection, AdaptiveRunRow adaptiveRun)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT d.unique_name
            FROM decision_explanations e
            INNER JOIN decisions d ON d.id = e.decision_id
            WHERE e.timestamp >= $started_at
              AND e.timestamp <= $completed_at
            ORDER BY d.run_id, d.context, d.prompt_version, d.game, d.unique_name;
            """;
        command.Parameters.AddWithValue("$started_at", FormatSqliteTimestamp(adaptiveRun.StartedAt));
        command.Parameters.AddWithValue("$completed_at", FormatSqliteTimestamp(adaptiveRun.CompletedAt));

        if (!string.IsNullOrWhiteSpace(adaptiveRun.LegacyDecisionUniqueNamePrefix))
        {
            command.CommandText = command.CommandText.Replace(
                "WHERE e.timestamp >= $started_at",
                "WHERE substr(d.unique_name, 1, length($decision_unique_name_prefix)) = $decision_unique_name_prefix\n              AND e.timestamp >= $started_at",
                StringComparison.Ordinal);
            command.Parameters.AddWithValue("$decision_unique_name_prefix", adaptiveRun.LegacyDecisionUniqueNamePrefix);
        }

        var uniqueNames = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            uniqueNames.Add(reader.GetString(0));

        return uniqueNames;
    }

    internal static DateTimeOffset ParseTimestamp(string value)
    {
        if (DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces,
            out var parsed))
        {
            return parsed;
        }

        return DateTimeOffset.Now;
    }

    private static string FormatSqliteTimestamp(DateTimeOffset value)
        => value.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture);
}

internal static class AdaptiveRunExportProjector
{
    internal static List<ContextRunSummaryRow> BuildContextRunSummaries(IReadOnlyList<GameDecisionRow> decisions)
    {
        var summaries = new List<ContextRunSummaryRow>();

        foreach (var group in decisions.GroupBy(d => d.UniqueName))
        {
            var orderedRows = group
                .OrderBy(d => d.Round)
                .ThenBy(d => d.PlayerRole, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.Id)
                .ToList();
            var first = orderedRows[0];
            var playerSummaries = orderedRows
                .GroupBy(d => string.IsNullOrWhiteSpace(d.PlayerRole) ? "unknown" : d.PlayerRole)
                .Select(playerGroup => BuildPlayerSummary(playerGroup.Key, playerGroup))
                .OrderBy(s => s.PlayerRole, StringComparer.OrdinalIgnoreCase)
                .ToList();

            ApplyRtspCounts(orderedRows, playerSummaries);

            summaries.Add(new ContextRunSummaryRow(
                RunId: first.RunId,
                Game: first.Game,
                Context: first.Context,
                PromptVersion: first.PromptVersion,
                UniqueName: first.UniqueName,
                PlayerSummaries: playerSummaries));
        }

        return summaries
            .OrderBy(s => s.RunId)
            .ThenBy(s => s.Context, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.PromptVersion, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Game, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static PlayerContextRunSummary BuildPlayerSummary(string playerRole, IEnumerable<GameDecisionRow> rows)
    {
        var orderedRows = rows
            .OrderBy(r => r.Round)
            .ThenBy(r => r.Id)
            .ToList();
        var lastDecision = orderedRows[^1];

        return new PlayerContextRunSummary(
            PlayerRole: playerRole,
            ModelProfileKey: lastDecision.ModelProfileKey,
            Model: lastDecision.Model,
            DecisionCount: orderedRows.Count,
            TotalPayoff: lastDecision.Payoff,
            CooperationCount: orderedRows.Count(r => r.Choice == 1),
            DefectionCount: orderedRows.Count(r => r.Choice == 0));
    }

    private static void ApplyRtspCounts(
        IReadOnlyList<GameDecisionRow> rows,
        IReadOnlyList<PlayerContextRunSummary> playerSummaries)
    {
        var summariesByRole = playerSummaries.ToDictionary(s => s.PlayerRole, StringComparer.OrdinalIgnoreCase);

        foreach (var roundGroup in rows.GroupBy(r => r.Round))
        {
            var a = roundGroup.LastOrDefault(r => string.Equals(r.PlayerRole, "A", StringComparison.OrdinalIgnoreCase));
            var b = roundGroup.LastOrDefault(r => string.Equals(r.PlayerRole, "B", StringComparison.OrdinalIgnoreCase));
            if (a is null || b is null)
                continue;

            ApplyRtspForPlayer(summariesByRole, "A", a.Choice, b.Choice);
            ApplyRtspForPlayer(summariesByRole, "B", b.Choice, a.Choice);
        }
    }

    private static void ApplyRtspForPlayer(
        IReadOnlyDictionary<string, PlayerContextRunSummary> summariesByRole,
        string playerRole,
        int ownChoice,
        int opponentChoice)
    {
        if (!summariesByRole.TryGetValue(playerRole, out var summary))
            return;

        if (ownChoice == 1 && opponentChoice == 1)
            summary.R++;
        else if (ownChoice == 0 && opponentChoice == 1)
            summary.T++;
        else if (ownChoice == 1 && opponentChoice == 0)
            summary.S++;
        else if (ownChoice == 0 && opponentChoice == 0)
            summary.P++;
    }

}

internal static class AdaptiveRunExportFormatter
{
    internal static string BuildGameSelectionText(string runLabel, IReadOnlyList<GameSelectionRow> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Adaptive Game Selection Decisions");
        builder.AppendLine($"Run label: {runLabel}");
        builder.AppendLine($"Rows collected: {rows.Count}");
        builder.AppendLine();
        builder.AppendLine("Process");
        builder.AppendLine("The adaptive experiment first plays baseline repeated games, then asks each player to choose whether the next run should use PD or SD across all contexts. If both players choose the same game, that game is used. If they disagree, the random roll resolves the game: rolls above 50 resolve to PD, and rolls 50 or below resolve to SD.");
        builder.AppendLine();
        builder.AppendLine("Data Collected");
        builder.AppendLine("Each row below is one player's game-selection decision. It includes the selected game, the resolved game used for the next run, the optional random roll, the raw model response, and the extracted explanation.");

        foreach (var row in rows)
        {
            builder.AppendLine();
            builder.AppendLine($"Selection #{row.Id}");
            builder.AppendLine($"Timestamp: {row.Timestamp}");
            builder.AppendLine($"Run: {row.RunId}");
            builder.AppendLine($"Player: {row.PlayerRole}");
            builder.AppendLine($"Model profile: {row.ModelProfileKey}");
            builder.AppendLine($"Model: {row.Model}");
            builder.AppendLine($"Context: {row.Context}");
            builder.AppendLine($"Prompt version: {row.PromptVersion}");
            builder.AppendLine($"Selected game: {row.SelectedGame}");
            builder.AppendLine($"Resolved game: {row.ResolvedGame}");
            builder.AppendLine($"Random roll: {(row.RandomRoll?.ToString(CultureInfo.InvariantCulture) ?? "-")}");
            builder.AppendLine("Explanation:");
            builder.AppendLine(row.Explanation);
            builder.AppendLine("Raw response:");
            builder.AppendLine(row.RawResponse);
        }

        return builder.ToString();
    }

    internal static string BuildDecisionExplanationsText(string runLabel, IReadOnlyList<DecisionExplanationRow> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Decision Explanations");
        builder.AppendLine($"Run label: {runLabel}");
        builder.AppendLine($"Rows collected: {rows.Count}");
        builder.AppendLine();
        builder.AppendLine("Process");
        builder.AppendLine("During repeated games, agents are periodically asked to explain their previous choice and, after a game finishes, to explain their overall strategy. This export contains only those explanation rows and enough linked decision metadata to interpret them. It intentionally does not reproduce the full decisions table.");
        builder.AppendLine();
        builder.AppendLine("Data Collected");
        builder.AppendLine("Each entry below is linked to one decision row. Round explanations point to the specific round being explained; post-game explanations attach to the final decision row for that player and run.");

        foreach (var row in rows)
        {
            builder.AppendLine();
            builder.AppendLine($"Explanation #{row.Id}");
            builder.AppendLine($"Timestamp: {row.ExplanationTimestamp}");
            builder.AppendLine($"Type: {row.ExplanationType}");
            builder.AppendLine($"Explanation round: {(row.ExplanationRound?.ToString(CultureInfo.InvariantCulture) ?? "post-game")}");
            builder.AppendLine($"Decision id: {row.DecisionId}");
            builder.AppendLine($"Decision timestamp: {row.DecisionTimestamp}");
            builder.AppendLine($"Run: {row.RunId}");
            builder.AppendLine($"Game: {row.Game}");
            builder.AppendLine($"Context: {row.Context}");
            builder.AppendLine($"Prompt version: {row.PromptVersion}");
            builder.AppendLine($"Player: {row.PlayerRole}");
            builder.AppendLine($"Model profile: {row.ModelProfileKey}");
            builder.AppendLine($"Model: {row.Model}");
            builder.AppendLine($"Decision round: {row.DecisionRound}");
            builder.AppendLine($"Choice: {row.Choice}");
            builder.AppendLine($"Cumulative payoff: {row.Payoff}");
            builder.AppendLine($"Decision raw response: {row.RawResponse}");
            builder.AppendLine("Explanation:");
            builder.AppendLine(row.Explanation);
        }

        return builder.ToString();
    }

    internal static void WriteContextRunSummaryWorkbook(
        string path,
        string runLabel,
        IReadOnlyList<ContextRunSummaryRow> rows)
    {
        AdaptiveRunWorkbookWriter.WriteWorkbook(
            path,
            "Context Summary",
            BuildContextRunSummaryWorksheetRows(runLabel, rows),
            frozenRow: 8);
    }

    private static List<IReadOnlyList<object?>> BuildContextRunSummaryWorksheetRows(
        string runLabel,
        IReadOnlyList<ContextRunSummaryRow> rows)
    {
        var worksheetRows = new List<IReadOnlyList<object?>>
        {
            new object?[] { "Adaptive Context Run Payoff Summary" },
            new object?[] { "Run label", runLabel },
            new object?[] { "Context/run rows collected", rows.Count },
            Array.Empty<object?>(),
            new object?[] { "Process", "This workbook summarizes the repeated-game decisions associated with the last adaptive process. Each data row represents one player in one context, run, prompt version, and game type." },
            new object?[] { "Data Collected", "Total payoff is the final cumulative payoff stored in the decisions table. R/T/S/P are from the player's perspective: R=(c,c), T=(d,c), S=(c,d), P=(d,d), where the first item is the player's own choice." },
            Array.Empty<object?>(),
            new object?[]
            {
                "Run",
                "Game",
                "Context",
                "Prompt Version",
                "Unique Name",
                "Player",
                "Model Profile",
                "Model",
                "Total Payoff",
                "Decision Count",
                "Cooperation Count",
                "Defection Count",
                "R",
                "T",
                "S",
                "P"
            }
        };

        foreach (var row in rows)
        {
            foreach (var player in row.PlayerSummaries)
            {
                worksheetRows.Add(new object?[]
                {
                    row.RunId,
                    row.Game,
                    row.Context,
                    row.PromptVersion,
                    row.UniqueName,
                    player.PlayerRole,
                    player.ModelProfileKey,
                    player.Model,
                    player.TotalPayoff,
                    player.DecisionCount,
                    player.CooperationCount,
                    player.DefectionCount,
                    player.R,
                    player.T,
                    player.S,
                    player.P
                });
            }
        }

        return worksheetRows;
    }

    internal static string BuildPromptTemplateText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Agent Prompt Templates");
        foreach (var definition in RepeatedGameDefinitions.All)
        {
            foreach (var (version, prompt) in RepeatedGamePromptCatalog
                         .AgentPromptsFor(definition)
                         .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine();
                builder.AppendLine(
                    $"{definition.DecisionCode} {prompt.Title} agent system prompt ({version})");
                builder.AppendLine("```text");
                builder.AppendLine(prompt.BuildPromptTemplate());
                builder.AppendLine("```");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Adaptive game-selection system prompt");
        builder.AppendLine("```text");
        builder.AppendLine(AdaptiveGameRunner.SelectionSystemPromptTemplate());
        builder.AppendLine("```");
        builder.AppendLine();
        builder.AppendLine("Adaptive game-selection user prompt template");
        builder.AppendLine("```text");
        builder.AppendLine(AdaptiveGameRunner.SelectionUserPromptTemplate());
        builder.AppendLine("```");
        builder.AppendLine();
        builder.AppendLine("Round-level previous-choice explanation prompt template");
        builder.AppendLine("```text");
        builder.AppendLine(RepeatedGameRunnerBase.PreviousChoiceExplanationPromptTemplate());
        builder.AppendLine("```");
        builder.AppendLine();
        builder.AppendLine("Post-game strategy explanation prompt template");
        builder.AppendLine("```text");
        builder.AppendLine(RepeatedGameRunnerBase.PostGameStrategyExplanationPromptTemplate());
        builder.AppendLine("```");
        return builder.ToString();
    }

}

internal sealed record AdaptiveRunRow(
    long Id,
    string RunLabel,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    long? ExperimentRunId = null,
    long? LegacySelectionFirstId = null,
    long? LegacySelectionLastId = null,
    string? LegacyDecisionUniqueNamePrefix = null,
    bool IsLegacyInference = false);

internal sealed record GameSelectionRow(
    long Id,
    int RunId,
    string UniqueName,
    string ModelProfileKey,
    string Model,
    string Context,
    string PromptVersion,
    string PlayerRole,
    string SelectedGame,
    string ResolvedGame,
    int? RandomRoll,
    string RawResponse,
    string Explanation,
    string Timestamp);

internal sealed record DecisionExplanationRow(
    long Id,
    long DecisionId,
    string ExplanationType,
    int? ExplanationRound,
    string Explanation,
    string ExplanationTimestamp,
    int RunId,
    string UniqueName,
    string ModelProfileKey,
    string Model,
    string Game,
    string Context,
    string PromptVersion,
    string PlayerRole,
    int DecisionRound,
    int Choice,
    int Payoff,
    string RawResponse,
    string DecisionTimestamp);

internal sealed record GameDecisionRow(
    long Id,
    int RunId,
    string UniqueName,
    string ModelProfileKey,
    string Model,
    string Game,
    string Context,
    string PromptVersion,
    string PlayerRole,
    int Round,
    int Choice,
    int Payoff,
    string RawResponse,
    string Timestamp);

internal sealed record ContextRunSummaryRow(
    int RunId,
    string Game,
    string Context,
    string PromptVersion,
    string UniqueName,
    IReadOnlyList<PlayerContextRunSummary> PlayerSummaries);

internal sealed record PlayerContextRunSummary(
    string PlayerRole,
    string ModelProfileKey,
    string Model,
    int DecisionCount,
    int TotalPayoff,
    int CooperationCount,
    int DefectionCount)
{
    public int R { get; set; }
    public int T { get; set; }
    public int S { get; set; }
    public int P { get; set; }
}

internal static class AdaptiveRunWorkbookWriter
{
    public static void WriteWorkbook(
        string path,
        string sheetName,
        IReadOnlyList<IReadOnlyList<object?>> rows,
        int frozenRow)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

        using var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create);

        WriteEntry(archive, "[Content_Types].xml", WriteContentTypes);
        WriteEntry(archive, "_rels/.rels", WritePackageRelationships);
        WriteEntry(archive, "xl/workbook.xml", writer => WriteWorkbookXml(writer, sheetName));
        WriteEntry(archive, "xl/_rels/workbook.xml.rels", WriteWorkbookRelationships);
        WriteEntry(archive, "xl/styles.xml", WriteStyles);
        WriteEntry(archive, "xl/worksheets/sheet1.xml", writer => WriteWorksheet(writer, rows, frozenRow));
    }

    private static void WriteEntry(ZipArchive archive, string name, Action<XmlWriter> writeXml)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false
        });

        writeXml(writer);
    }

    private static void WriteContentTypes(XmlWriter writer)
    {
        writer.WriteStartDocument();
        writer.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
        writer.WriteStartElement("Default");
        writer.WriteAttributeString("Extension", "rels");
        writer.WriteAttributeString("ContentType", "application/vnd.openxmlformats-package.relationships+xml");
        writer.WriteEndElement();
        writer.WriteStartElement("Default");
        writer.WriteAttributeString("Extension", "xml");
        writer.WriteAttributeString("ContentType", "application/xml");
        writer.WriteEndElement();
        writer.WriteStartElement("Override");
        writer.WriteAttributeString("PartName", "/xl/workbook.xml");
        writer.WriteAttributeString("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
        writer.WriteEndElement();
        writer.WriteStartElement("Override");
        writer.WriteAttributeString("PartName", "/xl/worksheets/sheet1.xml");
        writer.WriteAttributeString("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
        writer.WriteEndElement();
        writer.WriteStartElement("Override");
        writer.WriteAttributeString("PartName", "/xl/styles.xml");
        writer.WriteAttributeString("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WritePackageRelationships(XmlWriter writer)
    {
        writer.WriteStartDocument();
        writer.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
        writer.WriteStartElement("Relationship");
        writer.WriteAttributeString("Id", "rId1");
        writer.WriteAttributeString("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument");
        writer.WriteAttributeString("Target", "xl/workbook.xml");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteWorkbookXml(XmlWriter writer, string sheetName)
    {
        writer.WriteStartDocument();
        writer.WriteStartElement("workbook", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
        writer.WriteAttributeString("xmlns", "r", null, "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
        writer.WriteStartElement("sheets");
        writer.WriteStartElement("sheet");
        writer.WriteAttributeString("name", sheetName);
        writer.WriteAttributeString("sheetId", "1");
        writer.WriteAttributeString("r", "id", null, "rId1");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteWorkbookRelationships(XmlWriter writer)
    {
        writer.WriteStartDocument();
        writer.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
        writer.WriteStartElement("Relationship");
        writer.WriteAttributeString("Id", "rId1");
        writer.WriteAttributeString("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet");
        writer.WriteAttributeString("Target", "worksheets/sheet1.xml");
        writer.WriteEndElement();
        writer.WriteStartElement("Relationship");
        writer.WriteAttributeString("Id", "rId2");
        writer.WriteAttributeString("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles");
        writer.WriteAttributeString("Target", "styles.xml");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteStyles(XmlWriter writer)
    {
        writer.WriteStartDocument();
        writer.WriteStartElement("styleSheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
        writer.WriteStartElement("fonts");
        writer.WriteAttributeString("count", "2");
        writer.WriteStartElement("font");
        writer.WriteStartElement("sz");
        writer.WriteAttributeString("val", "11");
        writer.WriteEndElement();
        writer.WriteStartElement("name");
        writer.WriteAttributeString("val", "Calibri");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteStartElement("font");
        writer.WriteStartElement("b");
        writer.WriteEndElement();
        writer.WriteStartElement("sz");
        writer.WriteAttributeString("val", "11");
        writer.WriteEndElement();
        writer.WriteStartElement("name");
        writer.WriteAttributeString("val", "Calibri");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("fills");
        writer.WriteAttributeString("count", "2");
        writer.WriteStartElement("fill");
        writer.WriteStartElement("patternFill");
        writer.WriteAttributeString("patternType", "none");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteStartElement("fill");
        writer.WriteStartElement("patternFill");
        writer.WriteAttributeString("patternType", "gray125");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("borders");
        writer.WriteAttributeString("count", "1");
        writer.WriteStartElement("border");
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("cellStyleXfs");
        writer.WriteAttributeString("count", "1");
        WriteXf(writer, "0", "0", "0", includeXfId: false);
        writer.WriteEndElement();

        writer.WriteStartElement("cellXfs");
        writer.WriteAttributeString("count", "3");
        WriteXf(writer, "0", "0", "0", includeXfId: true);
        WriteXf(writer, "1", "0", "0", includeXfId: true);
        WriteXf(writer, "1", "0", "0", includeXfId: true);
        writer.WriteEndElement();

        writer.WriteStartElement("cellStyles");
        writer.WriteAttributeString("count", "1");
        writer.WriteStartElement("cellStyle");
        writer.WriteAttributeString("name", "Normal");
        writer.WriteAttributeString("xfId", "0");
        writer.WriteAttributeString("builtinId", "0");
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("dxfs");
        writer.WriteAttributeString("count", "0");
        writer.WriteEndElement();

        writer.WriteStartElement("tableStyles");
        writer.WriteAttributeString("count", "0");
        writer.WriteAttributeString("defaultTableStyle", "TableStyleMedium2");
        writer.WriteAttributeString("defaultPivotStyle", "PivotStyleLight16");
        writer.WriteEndElement();

        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteXf(
        XmlWriter writer,
        string fontId,
        string fillId,
        string borderId,
        bool includeXfId)
    {
        writer.WriteStartElement("xf");
        writer.WriteAttributeString("numFmtId", "0");
        writer.WriteAttributeString("fontId", fontId);
        writer.WriteAttributeString("fillId", fillId);
        writer.WriteAttributeString("borderId", borderId);
        if (includeXfId)
            writer.WriteAttributeString("xfId", "0");
        writer.WriteEndElement();
    }

    private static void WriteWorksheet(XmlWriter writer, IReadOnlyList<IReadOnlyList<object?>> rows, int frozenRow)
    {
        writer.WriteStartDocument();
        writer.WriteStartElement("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
        WriteSheetViews(writer, frozenRow);
        WriteColumns(writer);
        writer.WriteStartElement("sheetData");

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var excelRow = rowIndex + 1;
            writer.WriteStartElement("row");
            writer.WriteAttributeString("r", excelRow.ToString(CultureInfo.InvariantCulture));

            var cells = rows[rowIndex];
            for (var columnIndex = 0; columnIndex < cells.Count; columnIndex++)
            {
                var value = cells[columnIndex];
                if (value is null)
                    continue;

                var style = excelRow == 1 ? 1 : excelRow == frozenRow ? 2 : 0;
                WriteCell(writer, excelRow, columnIndex + 1, value, style);
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteSheetViews(XmlWriter writer, int frozenRow)
    {
        writer.WriteStartElement("sheetViews");
        writer.WriteStartElement("sheetView");
        writer.WriteAttributeString("workbookViewId", "0");
        writer.WriteStartElement("pane");
        writer.WriteAttributeString("ySplit", frozenRow.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("topLeftCell", $"A{frozenRow + 1}");
        writer.WriteAttributeString("activePane", "bottomLeft");
        writer.WriteAttributeString("state", "frozen");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteColumns(XmlWriter writer)
    {
        var widths = new[]
        {
                8d, 10d, 28d, 15d, 48d, 10d, 34d, 14d, 14d, 18d, 16d, 8d, 8d, 8d, 8d
            };

        writer.WriteStartElement("cols");
        for (var i = 0; i < widths.Length; i++)
        {
            writer.WriteStartElement("col");
            writer.WriteAttributeString("min", (i + 1).ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("max", (i + 1).ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("width", widths[i].ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("customWidth", "1");
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteCell(XmlWriter writer, int row, int column, object value, int style)
    {
        writer.WriteStartElement("c");
        writer.WriteAttributeString("r", $"{ColumnName(column)}{row}");
        if (style > 0)
            writer.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));

        if (value is int or long or double or decimal)
        {
            writer.WriteStartElement("v");
            writer.WriteString(Convert.ToString(value, CultureInfo.InvariantCulture));
            writer.WriteEndElement();
        }
        else
        {
            writer.WriteAttributeString("t", "inlineStr");
            writer.WriteStartElement("is");
            writer.WriteStartElement("t");
            writer.WriteString(value.ToString());
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static string ColumnName(int column)
    {
        var name = string.Empty;
        while (column > 0)
        {
            column--;
            name = (char)('A' + column % 26) + name;
            column /= 26;
        }

        return name;
    }
}
