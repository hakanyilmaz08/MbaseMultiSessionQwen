using System.Globalization;
using System.Diagnostics;

namespace SocialDilemmaLLMSimulation;

public enum CommandHandlingResult
{
    Continue,
    Exit
}

public sealed class ConsoleCommandHandler
{
    private readonly ExperimentSessionCoordinator _coordinator;
    private readonly string _helpText;

    public ConsoleCommandHandler(ExperimentSessionCoordinator coordinator, string helpText)
    {
        _coordinator = coordinator;
        _helpText = helpText;
    }

    public async Task<CommandHandlingResult> HandleAsync(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();

        try
        {
            switch (cmd)
            {
                case "/switch":
                    var sid = line.Length > cmd.Length ? line.Substring(cmd.Length).Trim() : string.Empty;
                    if (string.IsNullOrWhiteSpace(sid))
                    {
                        Console.WriteLine("usage: /switch <sid>");
                        return CommandHandlingResult.Continue;
                    }

                    _coordinator.SwitchSession(sid);
                    Console.WriteLine($"Switched to session: {_coordinator.ActiveSession}");
                    return CommandHandlingResult.Continue;

                case "/new":
                    if (parts.Length < 2) { Console.WriteLine("usage: /new <sid>"); return CommandHandlingResult.Continue; }
                    _coordinator.CreateSession(parts[1]);
                    Console.WriteLine($"Created & switched to: {_coordinator.ActiveSession}");
                    return CommandHandlingResult.Continue;

                case "/rename":
                    if (parts.Length < 3) { Console.WriteLine("usage: /rename <old> <new>"); return CommandHandlingResult.Continue; }
                    _coordinator.RenameSession(parts[1], parts[2]);
                    Console.WriteLine($"renamed {parts[1]} -> {parts[2]}");
                    return CommandHandlingResult.Continue;

                case "/delete":
                    if (parts.Length < 2) { Console.WriteLine("usage: /delete <sid>"); return CommandHandlingResult.Continue; }
                    Console.Write($"delete {parts[1]}? Type YES: ");
                    if ((Console.ReadLine()?.Trim()) == "YES")
                    {
                        _coordinator.DeleteSession(parts[1]);
                        Console.WriteLine("deleted.");
                    }
                    else
                    {
                        Console.WriteLine("aborted.");
                    }
                    return CommandHandlingResult.Continue;

                case "/temp":
                    if (parts.Length < 2) { Console.WriteLine("usage: /temp <float>"); return CommandHandlingResult.Continue; }
                    if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var newTemperature))
                    {
                        Console.WriteLine("temperature must be a number like 0.7");
                        return CommandHandlingResult.Continue;
                    }
                    _coordinator.SetTemperature(newTemperature);
                    Console.WriteLine($"temperature[{_coordinator.ActiveSession}] = {parts[1]}");
                    return CommandHandlingResult.Continue;

                case "/topp":
                    if (parts.Length < 2) { Console.WriteLine("usage: /topp <float>"); return CommandHandlingResult.Continue; }
                    if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var newTopP))
                    {
                        Console.WriteLine("top_p must be a number like 0.95");
                        return CommandHandlingResult.Continue;
                    }
                    _coordinator.SetTopP(newTopP);
                    Console.WriteLine($"top_p[{_coordinator.ActiveSession}] = {parts[1]}");
                    return CommandHandlingResult.Continue;

                case "/sys":
                    if (parts.Length < 2) { Console.WriteLine("usage: /sys <system prompt>"); return CommandHandlingResult.Continue; }
                    _coordinator.SetSystemPrompt(line.Substring(cmd.Length).Trim());
                    Console.WriteLine("server session system prompt updated.");
                    return CommandHandlingResult.Continue;

                case "/help":
                    Console.WriteLine(_helpText);
                    return CommandHandlingResult.Continue;

                case "/list":
                    var all = _coordinator.ListSessions();
                    Console.WriteLine(all.Count == 0 ? "(no sessions)" : string.Join(Environment.NewLine, all));
                    return CommandHandlingResult.Continue;

                case "/cfgmodels":
                    Console.WriteLine($"Current configuration: {_coordinator.CurrentSelection.Name} [{_coordinator.CurrentSelection.Source}]");
                    var selected = _coordinator.PromptForConfigurationSwitch();
                    Console.WriteLine(selected is null
                        ? "configuration unchanged."
                        : $"Created & switched to fresh session: {_coordinator.ActiveSession}");
                    return CommandHandlingResult.Continue;

                case "/where":
                    Console.WriteLine(_coordinator.DescribeCurrentSession());
                    return CommandHandlingResult.Continue;

                case "/save":
                    _coordinator.Save();
                    Console.WriteLine("saved.");
                    return CommandHandlingResult.Continue;

                case "/playipd":
                    await ExecuteRunnerAsync(
                        new IPDRunner(_coordinator),
                        1,
                        1,
                        (version, actualRunLabel, runId) => $"ipd_{version}_ethical_{actualRunLabel}_run{runId}.txt",
                        (actualRunLabel, runId) => $"ipd_ethicalv2_{actualRunLabel}_run{runId}.txt");
                    return CommandHandlingResult.Continue;

                case "/playisd":
                    await ExecuteRunnerAsync(
                        new ISDRunner(_coordinator),
                        1,
                        1,
                        (version, actualRunLabel, runId) => $"isd_{version}_ethicalv2_{actualRunLabel}_run{runId}.txt",
                        (actualRunLabel, runId) => $"isd_ethicalv2_{actualRunLabel}_run{runId}.txt");
                    return CommandHandlingResult.Continue;

                case "/playboth":
                    var modelLabel = BuildCrossModelLabel();
                    await ExecuteRunnerAsync(
                        new IPDRunner(_coordinator),
                        1,
                        10,
                        (version, _, runId) => $"ipd_{modelLabel}_{version}_run{runId}.txt",
                        (actualRunLabel, runId) => $"ipd_duration_{actualRunLabel}_run{runId}.txt");
                    await ExecuteRunnerAsync(
                        new ISDRunner(_coordinator),
                        1,
                        10,
                        (version, _, runId) => $"isd_{modelLabel}_{version}_run{runId}.txt",
                        (actualRunLabel, runId) => $"isd_duration_{actualRunLabel}_run{runId}.txt");
                    return CommandHandlingResult.Continue;

                case "/resetkeep":
                    _coordinator.ResetActiveSession(keepSystemPrompt: true);
                    Console.WriteLine("history cleared; system prompt kept.");
                    return CommandHandlingResult.Continue;

                case "/resetall":
                    _coordinator.ResetActiveSession(keepSystemPrompt: false);
                    Console.WriteLine("history + system prompt cleared.");
                    return CommandHandlingResult.Continue;

                case "/generate":
                    DecisionExporter.ExportPrettyFromDecisions("Data Source=ipd_results.db", AppContext.BaseDirectory);
                    return CommandHandlingResult.Continue;

                case "/exit":
                    Console.WriteLine("bye.");
                    return CommandHandlingResult.Exit;

                default:
                    Console.WriteLine("unknown command. /help for list.");
                    return CommandHandlingResult.Continue;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"error: {ex.Message}");
            return CommandHandlingResult.Continue;
        }
    }

    private async Task ExecuteRunnerAsync(
        RepeatedGameRunnerBase runner,
        int firstRunId,
        int lastRunId,
        Func<string, string, int, string> resultFileName,
        Func<string, int, string> durationFileName)
    {
        var runLabel = string.Empty;

        for (var runId = firstRunId; runId <= lastRunId; runId++)
        {
            var stopwatch = Stopwatch.StartNew();
            var (allResults, actualRunLabel) = await runner.RunV1ToV5SequentialAsync(runLabel, rounds: 50, resetPrompts: false, clearSessions: true, runId: runId);
            foreach (var kvp in allResults)
            {
                Console.WriteLine($"=== {kvp.Key} ===");
                Console.WriteLine(kvp.Value.Pretty());
                File.WriteAllText(resultFileName(kvp.Key, actualRunLabel, runId), kvp.Value.Pretty());
            }

            stopwatch.Stop();
            File.WriteAllText(durationFileName(actualRunLabel, runId), stopwatch.Elapsed.TotalSeconds.ToString());
        }
    }

    private string BuildCrossModelLabel()
    {
        return _coordinator.Models.Select(m => m.Model).Distinct().Skip(1).Any()
            ? "cross"
            : (string.IsNullOrWhiteSpace(_coordinator.Models.FirstOrDefault()?.Model)
                ? "noname"
                : _coordinator.Models.First().Model);
    }
}


