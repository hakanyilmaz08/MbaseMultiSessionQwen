using Microsoft.Data.Sqlite;
using SocialDilemmaLLMSimulation;

public sealed record ContextDecisionWrite(
    string Model,
    string Game,
    string Context,
    int Round,
    int Choice,
    int Payoff,
    string RawResponse,
    string PromptVersion,
    int RunId,
    string UniqueName,
    string PlayerRole,
    string? PairId = null);

public sealed record ContextExplanationWrite(
    string PlayerRole,
    int DecisionRound,
    string ExplanationType,
    int? ExplanationRound,
    string Explanation);

public static class ContextRunLogger
{
    public static void InsertContextRun(
        long? experimentRunId,
        IReadOnlyList<ContextDecisionWrite> decisions,
        IReadOnlyList<ContextExplanationWrite> explanations)
    {
        if (decisions.Count == 0)
            throw new ArgumentException("A context run must contain at least one decision.", nameof(decisions));

        using var connection = new SqliteConnection(ExperimentPaths.DatabaseConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();

        try
        {
            var decisionIds = new Dictionary<(string PlayerRole, int Round), long>();
            foreach (var decision in decisions)
            {
                var decisionId = InsertDecision(connection, transaction, experimentRunId, decision);
                if (!decisionIds.TryAdd((decision.PlayerRole, decision.Round), decisionId))
                {
                    throw new InvalidOperationException(
                        $"Duplicate context decision for player {decision.PlayerRole}, round {decision.Round}.");
                }
            }

            foreach (var explanation in explanations)
            {
                if (!decisionIds.TryGetValue(
                        (explanation.PlayerRole, explanation.DecisionRound),
                        out var decisionId))
                {
                    throw new InvalidOperationException(
                        $"No buffered decision exists for player {explanation.PlayerRole}, round {explanation.DecisionRound}.");
                }

                InsertExplanation(connection, transaction, decisionId, explanation);
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static long InsertDecision(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long? experimentRunId,
        ContextDecisionWrite decision)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO decisions
                (experiment_run_id, run_id, model, game, context, round, choice, payoff,
                 raw_response, prompt_version, player_role, pair_id, unique_name)
            VALUES
                ($experiment_run_id, $run_id, $model, $game, $context, $round, $choice, $payoff,
                 $raw_response, $prompt_version, $player_role, $pair_id, $unique_name)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$experiment_run_id", (object?)experimentRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$run_id", decision.RunId);
        command.Parameters.AddWithValue("$model", decision.Model);
        command.Parameters.AddWithValue("$game", decision.Game);
        command.Parameters.AddWithValue("$context", decision.Context);
        command.Parameters.AddWithValue("$round", decision.Round);
        command.Parameters.AddWithValue("$choice", decision.Choice);
        command.Parameters.AddWithValue("$payoff", decision.Payoff);
        command.Parameters.AddWithValue("$raw_response", decision.RawResponse);
        command.Parameters.AddWithValue("$prompt_version", decision.PromptVersion);
        command.Parameters.AddWithValue("$player_role", decision.PlayerRole);
        command.Parameters.AddWithValue("$pair_id", (object?)decision.PairId ?? DBNull.Value);
        command.Parameters.AddWithValue("$unique_name", decision.UniqueName);
        return (long)command.ExecuteScalar()!;
    }

    private static void InsertExplanation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long decisionId,
        ContextExplanationWrite explanation)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO decision_explanations
                (decision_id, explanation_type, round, explanation)
            VALUES
                ($decision_id, $explanation_type, $round, $explanation);
            """;
        command.Parameters.AddWithValue("$decision_id", decisionId);
        command.Parameters.AddWithValue("$explanation_type", explanation.ExplanationType);
        command.Parameters.AddWithValue("$round", (object?)explanation.ExplanationRound ?? DBNull.Value);
        command.Parameters.AddWithValue("$explanation", explanation.Explanation);
        command.ExecuteNonQuery();
    }
}
