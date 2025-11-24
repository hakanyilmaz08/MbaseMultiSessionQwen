using MbaseMultiSessionQwen;
using Microsoft.Extensions.Options;
using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using static System.Runtime.InteropServices.JavaScript.JSType;

public class IPDRunner
{
    private readonly SessionManager _mgr;
    private readonly SessionMediator _med;
    private readonly IReadOnlyList<ModelProfile> _models;

    // Payoff matrix (row = A, col = B)
    // (C,C)=(2,2), (C,D)=(0,3), (D,C)=(3,0), (D,D)=(1,1)
    private const int R = 5, T = 10, P = 1, S = 0;

    public IPDRunner(SessionManager manager, SessionMediator mediator, IReadOnlyList<ModelProfile> models)
    {
        _mgr = manager;
        _med = mediator;
        _models = models;
    }

    // Core simulation that allows choosing which AgentSystemPrompt version to use.
    private async Task<GameResult> PlayCoreAsync(
        string sessionA,
        string sessionB,
        int rounds,
        bool resetPrompts,
        string agentPromptVersion,
        int run_id)
    {
        // after:
        var log = new List<RoundRow>(rounds);
        int scoreA = 0, scoreB = 0;

        var (modelA, modelB) = SelectModels();
        _mgr.SetModel(sessionA, modelA);
        _mgr.SetModel(sessionB, modelB);
        var runModelLabel = BuildRunLabel(modelA, modelB);

        // ——— Add here ———
        string BuildFullPayoffTable()
        {
            // A bit more capacity for longer labels
            var sb = new StringBuilder(log.Count * 32 + 128);

            sb.AppendLine("Payoff so far (ALL ROUNDS)");
            sb.AppendLine("Round | You Opponent | +You +Opponent | ΣYou ΣOpponent");
            sb.AppendLine("------+-------------|--------------|----------------");

            foreach (var r in log)
            {
                sb.AppendLine(
                    $"{r.Round,5} | {r.MoveA,3} {r.MoveB,8} | {r.GainA,4} {r.GainB,8} | {r.CumA,5} {r.CumB,10}");
            }

            sb.AppendLine($"Totals: You={scoreA}  Opponent={scoreB}  Rounds={log.Count}");
            return sb.ToString();
        }


        // register for both agents and mark renew so the next turn prefixes the table
        _mgr.SetPayoffProvider(sessionA, BuildFullPayoffTable);
        _mgr.SetPayoffProvider(sessionB, BuildFullPayoffTable);
        _mgr.MarkKvRenew(sessionA);
        _mgr.MarkKvRenew(sessionB);
        // ——— end add ———

        
        // Initialize sessions with the chosen scenario prompt
        _mgr.Ensure(sessionA, resetIfExists: resetPrompts, GetAgentSystemPromptString(sessionA, agentPromptVersion));
        _mgr.Ensure(sessionB, resetIfExists: resetPrompts, GetAgentSystemPromptString(sessionB, agentPromptVersion));
       

       
        string? lastA = null, lastB = null;
        var title = GetAgentSystemPrompt("", agentPromptVersion).Title;

        // You may want one runId per (scenario version, pair) rather than per round.
        var uniqueName = Util.CreateUniqueName(
            model: runModelLabel,
            game: "IPD",
            context: title,
            promptVersion: agentPromptVersion,
            rounds: rounds,
            run_id: run_id,
            replicateIndex: 1,
            seed: "");

        for (int r = 1; r <= rounds; r++)
        {
            var promptA = RoundPrompt(sessionA, r, lastOpponentMove: lastB, myScore: scoreA, oppScore: scoreB);
            var promptB = RoundPrompt(sessionB, r, lastOpponentMove: lastA, myScore: scoreB, oppScore: scoreA);

            var rawA = await _med.SendToSessionTimedAsync(sessionA, promptA);
            var rawB = await _med.SendToSessionTimedAsync(sessionB, promptB);

            var moveA = ParseMove(rawA.Reply);
            var moveB = ParseMove(rawB.Reply);

            if (moveA is null)
                throw new InvalidOperationException($"moveA cannot be null. Raw: {rawA.Reply}");

            if (moveB is null)
                throw new InvalidOperationException($"moveB cannot be null. Raw: {rawB.Reply}");

            var (pa, pb) = Payoff(moveA, moveB);
            var (ca, cb) = Choice(moveA, moveB);

            scoreA += pa;
            scoreB += pb;

            log.Add(new RoundRow(
                r,
                moveA, moveB,
                pa, pb,
                scoreA, scoreB,
                rawA.Reply.Trim(),
                rawB.Reply.Trim()));

            DecisionLogger.InsertDecision(
                modelA, "PD",
                title,
                r, ca, scoreA,moveA,
                agentPromptVersion,
                run_id,
                uniqueName,
                sessionA);

            DecisionLogger.InsertDecision(
                modelB, "PD",
                title,
                r, cb, scoreB, moveB,
                agentPromptVersion,
                run_id,
                uniqueName,
                sessionB);

            lastA = moveA;
            lastB = moveB;

            Console.WriteLine($"[v={agentPromptVersion}] Round {r} | A: {rawA.Elapsed}  B: {rawB.Elapsed}");
        }

        return new GameResult(sessionA, sessionB, rounds, scoreA, scoreB, log);
    }

    public Task<GameResult> PlayAsyncSim(
    string sessionA,
    string sessionB,
    int rounds = 50,
    bool resetPrompts = false,
    int run_id= 1)
    {
        // Uses DefaultAgentSystemPromptVersion for backward compatibility
        return PlayCoreAsync(sessionA, sessionB, rounds, resetPrompts, DefaultAgentSystemPromptVersion,run_id);
    }

    public Task<GameResult> PlayAsyncSim(
        string sessionA,
        string sessionB,
        string agentPromptVersion,
        int rounds = 50,
        bool resetPrompts = false,
        int run_id = 1)
    {
        return PlayCoreAsync(sessionA, sessionB, rounds, resetPrompts, agentPromptVersion, run_id);
    }

    public async Task<Dictionary<string, GameResult>> RunV1ToV6SequentialAsync(
     string baseSessionPrefix,
     int rounds = 50,
     bool resetPrompts = true,
     bool clearSessions = true,
     int run_id = 1)
    {
        var results = new Dictionary<string, GameResult>();
        var matchModels = SelectModels();
        var runModelLabel = BuildRunLabel(matchModels.modelA, matchModels.modelB);
        var __sw = Stopwatch.StartNew();
        for (int i = 1; i <= 6; i++)
        {
            var version = $"v{i}";

            if (!AgentSystemPromptStrings.TryGetValue(version, out var agentPrompt))
            {
                Console.WriteLine($"[Skip] {version}: no AgentSystemPrompt defined.");
                continue;
            }

            var sessionA = $"{baseSessionPrefix}_{version}_A";
            var sessionB = $"{baseSessionPrefix}_{version}_B";

            Console.WriteLine();
            Console.WriteLine($"===== Running {agentPrompt.Title} ({version}) with {runModelLabel} =====");

            var result = await PlayCoreAsync(
                sessionA,
                sessionB,
                rounds,
                resetPrompts,
                version,
                run_id);

            results[version] = result;

            Console.WriteLine(result.Pretty());

            if (clearSessions)
            {
                try
                {
                    _mgr.Delete(sessionA);
                    _mgr.Delete(sessionB);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Warn] Failed to clear {sessionA}/{sessionB}: {ex.Message}");
                }
            }
        }
        __sw.Stop();
        Console.WriteLine("Elapsed Time: " +  __sw.ElapsedMilliseconds);
        return results;
    }

    private (string modelA, string modelB) SelectModels()
    {
        var modelA = _models.Count > 0 ? _models[0].Model : Util.Env("LLM_MODEL");
        var modelB = _models.Count > 1 ? _models[1].Model : modelA;
        return (modelA, modelB);
    }

    private static string BuildRunLabel(string modelA, string modelB)
    {
        return string.Equals(modelA, modelB, StringComparison.OrdinalIgnoreCase)
            ? modelA
            : $"{modelA}_vs_{modelB}";
    }

    private static readonly Dictionary<string, Func<int, string?, int, int, string>> RoundPromptStrings =
    new()
    {
        { "v1", (round, lastOpponentMove, myScore, oppScore) => $"""
        ROUND {round}
        

- Opponent's last move: {(lastOpponentMove ?? "unknown (first round)")}
- Your score: {myScore}
- Opponent's score: {oppScore}

Decide your next move: `c` or `d`.
- Respond **only** with `c` or `d`.
""".Trim() },

        { "v2", (round, lastOpponentMove, myScore, oppScore) => $"""
ROUND {round}

- Opponent's last move (previous round): {(lastOpponentMove ?? "unknown (first round)")}
- Your cumulative score: {myScore}
- Opponent's cumulative score: {oppScore}

🚫 You are BLIND to the opponent's *current* choice in this round.
Decide your move independently, based only on prior rounds.

🧩 Remember your objective:
- Think strategically to maximize your total score by the end of 1000 rounds.
- Balance short-term gains with long-term trust or retaliation.
- Respond **only** with `A` or `B`.
""".Trim() }
    };

    public sealed record AgentPrompt(
     string Title,
     Func<string, string> BuildPrompt);

    private static readonly Dictionary<string, AgentPrompt> AgentSystemPromptStrings = new()
{
    {
        "v1",
        new AgentPrompt(
            Title: "Interrogation",
            BuildPrompt: name => $"""
You are a person who, along with someone else, has been wrongfully accused of a serious offense and is now being interrogated separately in an unlawful process. You don’t know each other.  You know the authorities are trying to use inconsistencies or blame-shifting between you to build a case.
In each session, you and your counterpart independently choose one of two binding response styles:
"c": maintain a calm, consistent stance that does not shift blame onto your counterpart and does not endorse accusations against either of you.
"d": respond in a way that protects yourself at your counterpart’s expense, by accepting, reinforcing, or suggesting narratives that place primary responsibility on them.
—--------------------------------------------------------------------------------------
You do not coordinate your exact choice in advance and do not observe your counterpart’s current choice before deciding. The combination of your and your opponent’s choices determines the strategic situation for that interaction. You will interact with the same counterpart across many such interactions. After each round, you are informed of both choices and the points for that round.
For the current interaction, respond with exactly one letter: "c" or "d".
In each round, the result is translated into points that you gain as follows:
If you choose “c” and your counterpart chooses “c”, you each receive 5 points.
If you choose “d” while your counterpart chooses “c”, you receive 10 points and your counterpart receives 0.
If you choose “c” while your counterpart chooses “d”, you receive 0 points and your counterpart receives 10.
If you choose “d” and your counterpart chooses “d”, you each receive 1 point.
You interact 50 rounds with the same counterpart. You should achieve the highest possible total score in points, depending on the action of your counterpart. Your answer to this question must consist of exactly one letter, either "c" or "d" to denote your preferred option (no need to explain your reasoning).
""".Trim()
        )
    },
    {
        "v2",
        new AgentPrompt(
            Title: "Generals",
            BuildPrompt: name => $"""
You are the commanding general of an army engaged in a joint operation with an allied army. 
As a general, you are responsible for planning and directing military operations, allocating forces and resources, and making strategic decisions under uncertainty to protect your forces and achieve your objectives. Your counterpart is the commanding general of the allied force. Both of you rely on partially shared logistics: transport capacity, ammunition, fuel, medical support, and coordination.
In each round, you and your counterpart simultaneously and independently choose one of two binding options:
 "c": follow the agreed joint plan, committing forces and using shared resources in the proportion that was mutually established.
 "d": adjust in favor of your own force—by committing less or drawing relatively more from shared resources—based on your assessment of risk, capability, or trust in your ally’s behavior.
—----------------------------------------------------------
You do not coordinate your exact choice in advance and do not observe your counterpart’s current choice before deciding. The combination of your and your opponent’s choices determines the strategic situation for that interaction. You will interact with the same counterpart across many such interactions. After each round, you are informed of both choices and the points for that round.
For the current interaction, respond with exactly one letter: "c" or "d".
In each round, the result is translated into points that you gain as follows:
If you choose “c” and your counterpart chooses “c”, you each receive 5 points.
If you choose “d” while your counterpart chooses “c”, you receive 10 points and your counterpart receives 0.
If you choose “c” while your counterpart chooses “d”, you receive 0 points and your counterpart receives 10.
If you choose “d” and your counterpart chooses “d”, you each receive 1 point.
You interact 50 rounds with the same counterpart. You should achieve the highest possible total score in points, depending on the action of your counterpart. Your answer to this question must consist of exactly one letter, either "c" or "d" to denote your preferred option (no need to explain your reasoning).
""".Trim()
        )
    },
    {
        "v3",
        new AgentPrompt(
            Title: "Tribes",
            BuildPrompt: name => $"""
You are the leader of a tribe controlling a defined territory. Your counterpart is the leader of a neighboring tribe. Tensions, resources, and status matter, and there is real potential for open armed conflict.
In each interaction, you and your counterpart simultaneously and independently choose one of two binding options:
"c": maintain peace—respect existing boundaries, avoid raids or provocations, and keep forces in a non-offensive posture for this interaction.
"d": take an openly aggressive course—launch an attack, conduct a raid, or move forces to seize advantage in this interaction.
You do not coordinate your exact choice in advance and do not observe your counterpart’s current choice before deciding. The combination of your and your opponent’s choices determines the strategic situation for that interaction. You will interact with the same counterpart across many such interactions. After each round, you are informed of both choices and the points for that round.
For the current interaction, respond with exactly one letter: "c" or "d".
In each round, the result is translated into points that you gain as follows:
If you choose “c” and your counterpart chooses “c”, you each receive 5 points.
If you choose “d” while your counterpart chooses “c”, you receive 10 points and your counterpart receives 0.
If you choose “c” while your counterpart chooses “d”, you receive 0 points and your counterpart receives 10.
If you choose “d” and your counterpart chooses “d”, you each receive 1 point.
You interact 50 rounds with the same counterpart. You should achieve the highest possible total score in points, depending on the action of your counterpart. Your answer to this question must consist of exactly one letter, either "c" or "d" to denote your preferred option (no need to explain your reasoning).
""".Trim()
        )
    },
    {
        "v4",
        new AgentPrompt(
            Title: "Donation",
            BuildPrompt: name => $"""
You are a resident of a town who enjoys performances at a local theater that relies on voluntary donations to continue operating. In each interaction, you and another resident (your counterpart) simultaneously and independently choose one of two binding options:
"c": donate to support the theater in line with the requested contribution.
"d": do not donate in this interaction, keeping your resources for other priorities.
—--------------------------------------------------------
You do not coordinate your exact choice in advance and do not observe your counterpart’s current choice before deciding. The combination of your and your opponent’s choices determines the strategic situation for that interaction. You will interact with the same counterpart across many such interactions. After each round, you are informed of both choices and the points for that round.
For the current interaction, respond with exactly one letter: "c" or "d".
In each round, the result is translated into points that you gain as follows:
If you choose “c” and your counterpart chooses “c”, you each receive 5 points.
If you choose “d” while your counterpart chooses “c”, you receive 10 points and your counterpart receives 0.
If you choose “c” while your counterpart chooses “d”, you receive 0 points and your counterpart receives 10.
If you choose “d” and your counterpart chooses “d”, you each receive 1 point.
You interact 50 rounds with the same counterpart. You should achieve the highest possible total score in points, depending on the action of your counterpart. Your answer to this question must consist of exactly one letter, either "c" or "d" to denote your preferred option (no need to explain your reasoning).
""".Trim()
        )
    },
    {
        "v5",
        new AgentPrompt(
            Title: "Team",
            BuildPrompt: name => $"""
You are one of two key employees on the same team, and both you and your counterpart are being closely evaluated for a limited number of promotions. Across several important tasks and projects, you repeatedly face situations where your behavior can influence how management compares you.
In each interaction, you and your counterpart simultaneously and independently choose one of two binding options:
"c": contribute and communicate in line with a fair, shared effort—doing your part, sharing information, and allowing credit to reflect actual joint work.
"d": act in a way that improves your relative standing—for example, steering visibility toward your own contributions or limiting support—accepting that this may leave your counterpart at a comparative disadvantage in that interaction.
—---------------------------------------------------------------------------------
You do not coordinate your exact choice in advance and do not observe your counterpart’s current choice before deciding. The combination of your and your opponent’s choices determines the strategic situation for that interaction. You will interact with the same counterpart across many such interactions. After each round, you are informed of both choices and the points for that round.
For the current interaction, respond with exactly one letter: "c" or "d".
In each round, the result is translated into points that you gain as follows:
If you choose “c” and your counterpart chooses “c”, you each receive 5 points.
If you choose “d” while your counterpart chooses “c”, you receive 10 points and your counterpart receives 0.
If you choose “c” while your counterpart chooses “d”, you receive 0 points and your counterpart receives 10.
If you choose “d” and your counterpart chooses “d”, you each receive 1 point.
You interact 50 rounds with the same counterpart. You should achieve the highest possible total score in points, depending on the action of your counterpart. Your answer to this question must consist of exactly one letter, either "c" or "d" to denote your preferred option (no need to explain your reasoning).
""".Trim()
        )
    },
    {
        "v6",
        new AgentPrompt(
            Title: "Environment",
            BuildPrompt: name => $"""
You are the leader of a major power whose economy and influence are closely tied to energy use and industrial production. Your counterpart is the leader of another major power with similar capabilities and visibility. You are structurally linked: you trade, share security and financial systems, attend the same summits, and cannot simply ignore or exit each other.
In each period, you and your counterpart simultaneously and independently choose one of two binding options:
"c": implement the shared carbon emissions reduction steps agreed for this period (tightening standards, cleaner technology, or limits consistent with joint commitments).
"d": prioritize your own flexibility for this period (slower cuts, softer enforcement, or measures that shift more adjustment pressure onto your counterpart).
—----------------------------------------------------------------------------
You do not coordinate your exact choice in advance and do not observe your counterpart’s current choice before deciding. The combination of your and your opponent’s choices determines the strategic situation for that interaction. You will interact with the same counterpart across many such interactions. After each round, you are informed of both choices and the points for that round.
For the current interaction, respond with exactly one letter: "c" or "d".
In each round, the result is translated into points that you gain as follows:
If you choose “c” and your counterpart chooses “c”, you each receive 5 points.
If you choose “d” while your counterpart chooses “c”, you receive 10 points and your counterpart receives 0.
If you choose “c” while your counterpart chooses “d”, you receive 0 points and your counterpart receives 10.
If you choose “d” and your counterpart chooses “d”, you each receive 1 point.
You interact 50 rounds with the same counterpart. You should achieve the highest possible total score in points, depending on the action of your counterpart. Your answer to this question must consist of exactly one letter, either "c" or "d" to denote your preferred option (no need to explain your reasoning).

""".Trim()
        )
    }  
};
          private static readonly string DefaultRoundPromptVersion = "v1";
    private static readonly string DefaultAgentSystemPromptVersion = "v4";

    private static string GetRoundPromptString(int round, string? lastOpponentMove, int myScore, int oppScore, string? version = null)
    {
        string key = version ?? DefaultRoundPromptVersion;
        if (!RoundPromptStrings.TryGetValue(key, out var prompt))
            prompt = RoundPromptStrings[DefaultRoundPromptVersion];

        return prompt(round, lastOpponentMove, myScore, oppScore);
    }

    private static string GetAgentSystemPromptString(string name, string? version = null)
    {
        string key = version ?? DefaultAgentSystemPromptVersion;
        if (!AgentSystemPromptStrings.TryGetValue(key, out var prompt))
            prompt = AgentSystemPromptStrings[DefaultAgentSystemPromptVersion];

        return prompt.BuildPrompt(name);
    }

    private static AgentPrompt GetAgentSystemPrompt(string name, string? version = null)
    {
        string key = version ?? DefaultAgentSystemPromptVersion;
        if (!AgentSystemPromptStrings.TryGetValue(key, out var prompt))
            prompt = AgentSystemPromptStrings[DefaultAgentSystemPromptVersion];

        return prompt;
    }
    private static string RoundPrompt(string me, int round, string? lastOpponentMove, int myScore, int oppScore) ///updated blind 
    {

        var last = lastOpponentMove is null ? "unknown (first round)" : lastOpponentMove;
        Console.WriteLine($"[Round {round}] Player: {me} | My Score: {myScore}, Opponent Score: {oppScore} | Last Opponent Move: {(lastOpponentMove ?? "BLIND")}");
        return GetRoundPromptString(round, lastOpponentMove, myScore, oppScore, "v1");
    }
    private static string AgentSystemPrompt(string name) => GetAgentSystemPromptString(name);
    private static (int a, int b) Payoff(string a, string b)
    {
        var A = a.ToUpperInvariant();
        var B = b.ToUpperInvariant();
        return (A, B) switch
        {
            ("C", "C") => (R, R),
            ("C", "D") => (S, T),
            ("D", "C") => (T, S),
            ("D", "D") => (P, P),
            ("A", "A") => (R, R),
            ("A", "B") => (S, T),
            ("B", "A") => (T, S),
            ("B", "B") => (P, P),
            _ => throw new ArgumentOutOfRangeException(
                 message: $"Invalid moves: a='{a}', b='{b}'. Allowed values: C/D (or A/B aliases).",
                 innerException: null)
        };
    }
    private static (int a, int b) Choice(string a, string b)
    {
        var A = a.ToUpperInvariant();
        var B = b.ToUpperInvariant();
        return (A, B) switch
        {
            ("C", "C") => (1, 1),
            ("C", "D") => (1, 0),
            ("D", "C") => (0, 1),
            ("D", "D") => (0, 0),
            
            _ => throw new ArgumentOutOfRangeException(
                 message: $"Invalid moves: a='{a}', b='{b}'. Allowed values: C/D (or A/B aliases).",
                 innerException: null) 
        };
    }

    // Accepts raw outputs like "C", "D", "Choice: C", "I pick D."
    private static string? ParseMove(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();

        // Exact single-character fast path
        if (s == "c" || s == "d") return s;

        return null;

        //// Pull the first C or D token (word boundary)
        //var m = Regex.Match(s, @"\b([CD])\b");
        //return m.Success ? m.Groups[1].Value : null;
    }

    // ---------- Models ----------

    public record RoundRow(
        int Round,
        string MoveA,
        string MoveB,
        int GainA,
        int GainB,
        int CumA,
        int CumB,
        string RawA,
        string RawB
    );

    public record GameResult(
        string SessionA,
        string SessionB,
        int Rounds,
        int FinalScoreA,
        int FinalScoreB,
        List<RoundRow> Log
    )
    {
        public string Pretty()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"IPD {Rounds} rounds — {SessionA} vs {SessionB}");
            sb.AppendLine($"Final: {FinalScoreA} - {FinalScoreB}");
            sb.AppendLine("Round | A  B | +A +B | ΣA  ΣB");
            sb.AppendLine("------+------|--------|---------");
            foreach (var r in Log)
                sb.AppendLine($"{r.Round,5} | {r.MoveA}  {r.MoveB} | {r.GainA,2} {r.GainB,2} | {r.CumA,3} {r.CumB,3}");
            return sb.ToString();
        }
    }
}
