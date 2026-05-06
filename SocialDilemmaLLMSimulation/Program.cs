using SocialDilemmaLLMSimulation;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var store = Util.Env("SESSIONS_PATH");
var mode = Util.Env("MBASE_SESSION_MODE").ToLowerInvariant();

const string HelpText = """
Commands:
  /help                      Show this help
  /list                      List sessions
  /switch <sid>              Switch active session (creates if missing)
  /new <sid>                 Create and switch to a new session
  /rename <old> <new>        Rename a session
  /delete <sid>              Delete a session
  /temp <value>              Override temperature for current session (e.g., /temp 0.3)
  /topp <value>              Override top_p for current session (e.g., /topp 0.9)
  /where                     Show current session + settings (+ conversation_id if any)
  /cfgmodels                 Switch model/provider configuration
  /save                      Force save to disk
  /playipd                   Play iterated prisoner's dilemma
  /playisd                   Play snowdrift (iterated)
  /playboth                  Play both games (snowdrift and prisoner's dilemma) sequentially
  /playadaptive              Play adaptive game-selection experiment
  /generate                  Export decision logs to text files
  /exit                      Quit

Anything not starting with '/' is sent to the model in the current session.
""";

var startupSessionsPath = Path.Combine(AppContext.BaseDirectory, "sessions.json");
if (File.Exists(startupSessionsPath))
{
    File.SetAttributes(startupSessionsPath, File.GetAttributes(startupSessionsPath) & ~FileAttributes.ReadOnly);
    File.Delete(startupSessionsPath);
}

Console.OutputEncoding = Encoding.UTF8;

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
};

using var coordinator = new ExperimentSessionCoordinator(store, jsonOptions, mode);
var createdFreshStartupSession = coordinator.Initialize();
var commandHandler = new ConsoleCommandHandler(coordinator, HelpText);

Console.WriteLine($"Active session: {coordinator.ActiveSession}");
if (createdFreshStartupSession)
    Console.WriteLine($"Created fresh session for current configuration: {coordinator.ActiveSession}");
Console.WriteLine("Type /help for commands.\n");
DbInit.EnsureCreated();

while (true)
{
    Console.Write("> ");
    var line = Console.ReadLine();
    if (line is null)
        break;

    line = line.Trim();
    if (line.Length == 0)
        continue;

    if (line.StartsWith("/"))
    {
        var result = await commandHandler.HandleAsync(line);
        if (result == CommandHandlingResult.Exit)
            return;

        continue;
    }

    try
    {
        var activeSession = coordinator.EnsureDefaultActiveSession();
        var reply = await coordinator.Manager.SendAsync(activeSession, line);
        Console.WriteLine($"[assistant] {reply}\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"error sending message: {ex.Message}");
    }
}
