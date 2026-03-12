using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using SocialDilemmaLLMSimulation;

public static class DecisionExporter
{
    public static int ExportPrettyFromDecisions(string connectionString, string outputFolder)
    {
        var resolvedOutputFolder = ExperimentPaths.EnsureDirectory(outputFolder);

        using var conn = new SqliteConnection(connectionString);
        conn.Open();

        // 1) Get all games (unique_name values)
        var uniqueNames = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT unique_name FROM decisions ORDER BY unique_name;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                uniqueNames.Add(reader.GetString(0));
            }
        }

        var exportedCount = 0;
        foreach (var uniqueName in uniqueNames)
        {
            // 2) Load all rows for this game
            var rounds = new SortedDictionary<int, (Row? A, Row? B)>();
            string? game = null;
            string? context = null;
            string? modelA = null;
            string? modelB = null;

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT round, player_role, payoff, raw_response, model, game, context
                    FROM decisions
                    WHERE unique_name = $uniqueName
                    ORDER BY round, player_role;";
                cmd.Parameters.AddWithValue("$uniqueName", uniqueName);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    int round = reader.GetInt32(0);
                    string role = reader.GetString(1);
                    double payoff = reader.GetDouble(2);
                    string rawResponse = reader.IsDBNull(3) ? "" : reader.GetString(3);
                    string model = reader.GetString(4);
                    game ??= reader.GetString(5);
                    context ??= reader.GetString(6);

                    if (!rounds.TryGetValue(round, out var pair))
                        pair = (null, null);

                    var row = new Row
                    {
                        Round = round,
                        Role = role,
                        PayoffCum = payoff,
                        RawResponse = rawResponse,
                        Model = model
                    };

                    if (string.Equals(role, "A", StringComparison.OrdinalIgnoreCase))
                    {
                        pair.A = row;
                        modelA ??= model;
                    }
                    else if (string.Equals(role, "B", StringComparison.OrdinalIgnoreCase))
                    {
                        pair.B = row;
                        modelB ??= model;
                    }

                    rounds[round] = pair;
                }
            }

            if (rounds.Count == 0)
                continue; // nothing to export for this unique_name

            // 3) Build Pretty-style text
            var sb = new StringBuilder();

            int totalRounds = rounds.Keys.Max();
            string sessionA = modelA ?? "Player A";
            string sessionB = modelB ?? "Player B";

            // Use IPD if present in unique_name, otherwise fallback to game column.
            string headerGame = uniqueName.Contains("__IPD__", StringComparison.OrdinalIgnoreCase)
                ? "IPD"
                : (game ?? "ISD");

            // Final cumulative scores taken from the last round
            var lastPair = rounds[rounds.Keys.Max()];
            double finalA = lastPair.A?.PayoffCum ?? 0;
            double finalB = lastPair.B?.PayoffCum ?? 0;

            sb.AppendLine($"{headerGame} {totalRounds} rounds — {sessionA} vs {sessionB}");
            if (!string.IsNullOrWhiteSpace(context))
                sb.AppendLine($"Context: {context}");
            sb.AppendLine($"Final: {finalA} - {finalB}");
            sb.AppendLine("Round | A  B | +A +B | ΣA  ΣB");
            sb.AppendLine("------+------|--------|---------");

            double prevCumA = 0;
            double prevCumB = 0;

            foreach (var kvp in rounds)
            {
                int r = kvp.Key;
                var a = kvp.Value.A;
                var b = kvp.Value.B;

                double cumA = a?.PayoffCum ?? prevCumA;
                double cumB = b?.PayoffCum ?? prevCumB;

                double gainA = cumA - prevCumA;
                double gainB = cumB - prevCumB;

                char moveA = ToMoveChar(a?.RawResponse);
                char moveB = ToMoveChar(b?.RawResponse);

                sb.AppendLine(
                    $"{r,5} | {moveA}  {moveB} | {gainA,2} {gainB,2} | {cumA,3} {cumB,3}");

                prevCumA = cumA;
                prevCumB = cumB;
            }

            // 4) Write to file, using unique_name as file name
            string safeFileName = MakeSafeFileName(uniqueName) + ".txt";
            string fullPath = Path.Combine(resolvedOutputFolder, safeFileName);
            File.WriteAllText(fullPath, sb.ToString());
            exportedCount++;
        }

        return exportedCount;
    }

    private sealed class Row
    {
        public int Round { get; set; }
        public string Role { get; set; } = "";
        public double PayoffCum { get; set; }
        public string RawResponse { get; set; } = "";
        public string Model { get; set; } = "";
    }

    private static char ToMoveChar(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return '?';

        var trimmed = raw.Trim();
        // Treat anything starting with 'c' as cooperate, otherwise defect.
        return trimmed.StartsWith("c", StringComparison.OrdinalIgnoreCase) ? 'C' : 'D';
    }

    private static string MakeSafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            sb.Append(invalid.Contains(c) ? '_' : c);
        }
        return sb.ToString();
    }
}
