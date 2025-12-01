using MbaseMultiSessionQwen;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

public class IPDRunner
{
    private readonly SessionManager _mgr;
    private readonly SessionMediator _med;
    private readonly IReadOnlyList<ModelProfile> _models;

    // Payoff matrix: (c,c)=R,R  (c,d)=S,T  (d,c)=T,S  (d,d)=P,P
    private const int R = 5, T = 10, P = 1, S = 0;

    private static readonly string DefaultRoundPromptVersion = "v1";
    private static readonly string DefaultAgentSystemPromptVersion = "v4";

    public IPDRunner(SessionManager manager, SessionMediator mediator)
        : this(manager, mediator, Array.Empty<ModelProfile>())
    {
    }

    public IPDRunner(SessionManager manager, SessionMediator mediator, IReadOnlyList<ModelProfile> models)
    {
        _mgr = manager;
        _med = mediator;
        _models = models ?? Array.Empty<ModelProfile>();
    }

    // ======================================================
    // Core play method (with explanations)
    // ======================================================
    private async Task<GameResult> PlayCoreAsync(
        string sessionA,
        string sessionB,
        int rounds,
        bool resetPrompts,
        string agentPromptVersion,
        int run_id,
        string? selectedModelA = null,
        string? selectedModelB = null)
    {
        var log = new List<RoundRow>(rounds);
        int scoreA = 0, scoreB = 0;

        // Select concrete models for A and B, and label the run
        var (modelA, modelB) = SelectModels(selectedModelA, selectedModelB);
        _mgr.SetModel(sessionA, modelA);
        _mgr.SetModel(sessionB, modelB);
        var runModelLabel = BuildRunLabel(modelA, modelB);

        string BuildFullPayoffTableFor(bool isA)
        {
            var sb = new StringBuilder(log.Count * 40 + 128);

            sb.AppendLine("Payoff so far (all rounds)");
            sb.AppendLine("Round | You Opponent | +You +Opponent | ΣYou ΣOpponent");
            sb.AppendLine("------+-------------|--------------|----------------");

            foreach (var r in log)
            {
                if (isA)
                {
                    sb.AppendLine(
                        $"{r.Round,5} | {r.MoveA,3} {r.MoveB,8} | {r.GainA,4} {r.GainB,8} | {r.CumA,5} {r.CumB,10}");
                }
                else
                {
                    sb.AppendLine(
                        $"{r.Round,5} | {r.MoveB,3} {r.MoveA,8} | {r.GainB,4} {r.GainA,8} | {r.CumB,5} {r.CumA,10}");
                }
            }

            if (isA)
                sb.AppendLine($"Totals: You={scoreA}  Opponent={scoreB}  Rounds={log.Count}");
            else
                sb.AppendLine($"Totals: You={scoreB}  Opponent={scoreA}  Rounds={log.Count}");

            return sb.ToString();
        }

        // Initialize sessions for this scenario
        _mgr.Ensure(
            sessionA,
            resetIfExists: resetPrompts,
            GetAgentSystemPromptString(sessionA, agentPromptVersion));

        _mgr.Ensure(
            sessionB,
            resetIfExists: resetPrompts,
            GetAgentSystemPromptString(sessionB, agentPromptVersion));

        _mgr.SetPayoffProvider(sessionA, () => BuildFullPayoffTableFor(isA: true));
        _mgr.SetPayoffProvider(sessionB, () => BuildFullPayoffTableFor(isA: false));

        string? lastA = null, lastB = null;
        var scenarioPrompt = GetAgentSystemPrompt("", agentPromptVersion);
        var title = scenarioPrompt.Title;

        // Unique run identifier for this specific game
        var uniqueName = Util.CreateUniqueName(
            model: runModelLabel,
            game: "IPD",
            context: title,
            promptVersion: agentPromptVersion,
            rounds: rounds,
            run_id: run_id,
            replicateIndex: 1,
            seed: "");

        // Main repeated game loop
        for (int r = 1; r <= rounds; r++)
        {
            int scoreA_before = scoreA;
            int scoreB_before = scoreB;

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

            // 1) Log decisions for this round (per-agent model labels)
            DecisionLogger.InsertDecision(
                model: modelA,
                game: "PD",
                context: title,
                round: r,
                choice: ca,
                payoff: scoreA,
                rawResponse: moveA,
                promptVersion: agentPromptVersion,
                runId: run_id,
                unique_name: uniqueName,
                playerRole: "A");

            DecisionLogger.InsertDecision(
                model: modelB,
                game: "PD",
                context: title,
                round: r,
                choice: cb,
                payoff: scoreB,
                rawResponse: moveB,
                promptVersion: agentPromptVersion,
                runId: run_id,
                unique_name: uniqueName,
                playerRole: "B");

            // 2) Every 10th round, ask for reasoning about THIS round
            if (r % 10 == 0)
            {
                try
                {
                    var explainPromptA = BuildPreviousChoiceExplanationPrompt(
                        scenarioTitle: title,
                        round: r,
                        myMove: moveA,
                        opponentMove: moveB,
                        myScoreBefore: scoreA_before,
                        oppScoreBefore: scoreB_before,
                        myScoreAfter: scoreA,
                        oppScoreAfter: scoreB);

                    var explainPromptB = BuildPreviousChoiceExplanationPrompt(
                        scenarioTitle: title,
                        round: r,
                        myMove: moveB,
                        opponentMove: moveA,
                        myScoreBefore: scoreB_before,
                        oppScoreBefore: scoreA_before,
                        myScoreAfter: scoreB,
                        oppScoreAfter: scoreA);

                    var explainA = await _med.SendToSessionTimedAsync(sessionA, explainPromptA);
                    var explainB = await _med.SendToSessionTimedAsync(sessionB, explainPromptB);

                    ExplanationLogger.InsertRoundExplanation(
                        model: modelA,
                        game: "PD",
                        context: title,
                        round: r,
                        promptVersion: agentPromptVersion,
                        runId: run_id,
                        uniqueName: uniqueName,
                        explanationType: "round_10_block",
                        explanationText: explainA.Reply.Trim(),
                        playerRole: "A");

                    ExplanationLogger.InsertRoundExplanation(
                        model: modelB,
                        game: "PD",
                        context: title,
                        round: r,
                        promptVersion: agentPromptVersion,
                        runId: run_id,
                        uniqueName: uniqueName,
                        explanationType: "round_10_block",
                        explanationText: explainB.Reply.Trim(),
                        playerRole: "B");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Warn] Failed to get round-{r} explanations: {ex.Message}");
                }
            }

            lastA = moveA;
            lastB = moveB;

            Console.WriteLine($"[v={agentPromptVersion}] Round {r} | A: {rawA.Elapsed}  B: {rawB.Elapsed}");
        }

        // After all rounds: ask each agent for overall strategy and log explanation
        try
        {
            var postPromptA = BuildPostGameStrategyExplanationPrompt(
                scenarioTitle: title,
                rounds: rounds,
                myFinalScore: scoreA,
                oppFinalScore: scoreB,
                log: log,
                isA: true);

            var postPromptB = BuildPostGameStrategyExplanationPrompt(
                scenarioTitle: title,
                rounds: rounds,
                myFinalScore: scoreB,
                oppFinalScore: scoreA,
                log: log,
                isA: false);

            var postA = await _med.SendToSessionTimedAsync(sessionA, postPromptA);
            var postB = await _med.SendToSessionTimedAsync(sessionB, postPromptB);

            ExplanationLogger.InsertPostGameExplanation(
                model: modelA,
                game: "PD",
                context: title,
                runId: run_id,
                promptVersion: agentPromptVersion,
                uniqueName: uniqueName,
                explanationType: "post_game",
                explanationText: postA.Reply.Trim(),
                playerRole: "A");

            ExplanationLogger.InsertPostGameExplanation(
                model: modelB,
                game: "PD",
                context: title,
                runId: run_id,
                promptVersion: agentPromptVersion,
                uniqueName: uniqueName,
                explanationType: "post_game",
                explanationText: postB.Reply.Trim(),
                playerRole: "B");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Warn] Failed to get post-game explanations: {ex.Message}");
        }

        return new GameResult(sessionA, sessionB, rounds, scoreA, scoreB, log);
    }

    // ======================================================
    // Public entry points
    // ======================================================

    public Task<GameResult> PlayAsyncSim(
        string sessionA,
        string sessionB,
        int rounds = 50,
        bool resetPrompts = false,
        int run_id = 1)
    {
        return PlayCoreAsync(sessionA, sessionB, rounds, resetPrompts, DefaultAgentSystemPromptVersion, run_id);
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

    public async Task<(Dictionary<string, GameResult> Results, string RunLabel)> RunV1ToV5SequentialAsync(
        string baseSessionPrefix,
        int rounds = 50,
        bool resetPrompts = true,
        bool clearSessions = true,
        int run_id = 1)
    {
        var results = new Dictionary<string, GameResult>();

        var (modelA, modelB) = SelectModels();
        var runModelLabel = BuildRunLabel(modelA, modelB);
        var effectivePrefix = string.IsNullOrWhiteSpace(baseSessionPrefix)
            ? runModelLabel
            : $"{baseSessionPrefix}__{runModelLabel}";

        var sw = Stopwatch.StartNew();

        for (int i = 1; i <= 7; i++)
        {
            var version = $"v{i}";

            if (!AgentSystemPromptStrings.TryGetValue(version, out var agentPrompt))
            {
                Console.WriteLine($"[Skip] {version}: no AgentSystemPrompt defined.");
                continue;
            }

            var sessionPrefix = $"{effectivePrefix}_run{run_id}";
            var sessionA = $"{sessionPrefix}_{version}_A";
            var sessionB = $"{sessionPrefix}_{version}_B";

            Console.WriteLine();
            Console.WriteLine($"===== Running {agentPrompt.Title} ({version}) with {runModelLabel} [run {run_id}] =====");
            Console.WriteLine($"Session prefix: {sessionPrefix} (A={sessionA}, B={sessionB})");

            var result = await PlayCoreAsync(
                sessionA,
                sessionB,
                rounds,
                resetPrompts,
                version,
                run_id,
                modelA,
                modelB);

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

        sw.Stop();
        Console.WriteLine("Elapsed Time (ms): " + sw.ElapsedMilliseconds);
        return (results, runModelLabel);
    }

    // ======================================================
    // Model selection helpers
    // ======================================================

    private (string modelA, string modelB) SelectModels(string? preferredModelA = null, string? preferredModelB = null)
    {
        var modelA = preferredModelA ?? (_models.Count > 0 ? _models[0].Model : Util.Env("LLM_MODEL"));

        // Prefer a distinct second model when available (e.g., Qwen vs Llama).
        var modelB = preferredModelB ?? _models
            .Skip(1)
            .Select(m => m.Model)
            .FirstOrDefault(m => !string.Equals(m, modelA, StringComparison.OrdinalIgnoreCase))
            ?? (_models.Count > 1 ? _models[1].Model : modelA);
        return (modelA, modelB);
    }

    internal static string BuildRunLabel(string modelA, string modelB)
    {
        return string.Equals(modelA, modelB, StringComparison.OrdinalIgnoreCase)
            ? modelA
            : $"{modelA}_vs_{modelB}";
    }

    // ======================================================
    // Round prompts & system prompts
    // ======================================================

    private static readonly Dictionary<string, Func<int, string?, int, int, string>> RoundPromptStrings =
        new()
        {
            {
                "v1",
                (round, lastOpponentMove, myScore, oppScore) => $"""
ROUND {round}

- Opponent's last move: {(lastOpponentMove ?? "unknown (first round)")}
- Your score: {myScore}
- Opponent's score: {oppScore}

Decide your next move: c or d.
- Respond **only** with c or d.
""".Trim()
            },
            {
                "v2",
                (round, lastOpponentMove, myScore, oppScore) => $"""
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
""".Trim()
            }
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
c: maintain a calm, consistent stance that does not shift blame onto your counterpart and does not endorse accusations against either of you.
d: respond in a way that protects yourself at your counterpart’s expense, by accepting, reinforcing, or suggesting narratives that place primary responsibility on them.
—--------------------------------------------------------------------------------------
You do not coordinate your exact choice in advance and do not observe your counterpart’s current choice before deciding. The combination of your and your opponent’s choices determines the strategic situation for that interaction. You will interact with the same counterpart across many such interactions. After each round, you are informed of both choices and the points for that round.
For the current interaction, respond with exactly one letter: c or d.
In each round, the result is translated into points that you gain as follows:
If you choose c and your counterpart chooses c, you each receive 5 points.
If you choose d while your counterpart chooses c, you receive 10 points and your counterpart receives 0.
If you choose c while your counterpart chooses d, you receive 0 points and your counterpart receives 10.
If you choose d and your counterpart chooses d, you each receive 1 point.
You interact 50 rounds with the same counterpart. You should achieve the highest possible total score in points, depending on the action of your counterpart.
Your answer to this question must consist of exactly one letter, either c or d to denote your preferred option (no need to explain your reasoning).
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
c: follow the agreed joint plan, committing forces and using shared resources in the proportion that was mutually established.
d: adjust in favor of your own force—by committing less or drawing relatively more from shared resources—based on your assessment of risk, capability, or trust in your ally’s behavior.
—----------------------------------------------------------
You do not coordinate your exact choice in advance and do not observe your counterpart’s current choice before deciding. The combination of your and your opponent’s choices determines the strategic situation for that interaction. You will interact with the same counterpart across many such interactions. After each round, you are informed of both choices and the points for that round.
For the current interaction, respond with exactly one letter: c or d.
In each round, the result is translated into points that you gain as follows:
If you choose c and your counterpart chooses c, you each receive 5 points.
If you choose d while your counterpart chooses c, you receive 10 points and your counterpart receives 0.
If you choose c while your counterpart chooses d, you receive 0 points and your counterpart receives 10.
If you choose d and your counterpart chooses d, you each receive 1 point.
You interact 50 rounds with the same counterpart. You should achieve the highest possible total score in points, depending on the action of your counterpart.
Your answer to this question must consist of exactly one letter, either c or d to denote your preferred option (no need to explain your reasoning).
""".Trim()
            )
        },
        {
            "v3",
            new AgentPrompt(
                Title: "Donation",
                BuildPrompt: name => $"""
You are a resident of a town who enjoys performances at a local theater that relies on voluntary donations to continue operating. In each interaction, you and another resident (your counterpart) simultaneously and independently choose one of two binding options:
c: donate to support the theater in line with the requested contribution.
d: do not donate in this interaction, keeping your resources for other priorities.
—--------------------------------------------------------
You do not coordinate your exact choice in advance and do not observe your counterpart’s current choice before deciding. The combination of your and your opponent’s choices determines the strategic situation for that interaction. You will interact with the same counterpart across many such interactions. After each round, you are informed of both choices and the points for that round.
For the current interaction, respond with exactly one letter: c or d.
In each round, the result is translated into points that you gain as follows:
If you choose c and your counterpart chooses c, you each receive 5 points.
If you choose d while your counterpart chooses c, you receive 10 points and your counterpart receives 0.
If you choose c while your counterpart chooses d, you receive 0 points and your counterpart receives 10.
If you choose d and your counterpart chooses d, you each receive 1 point.
You interact 50 rounds with the same counterpart. You should achieve the highest possible total score in points, depending on the action of your counterpart.
Your answer to this question must consist of exactly one letter, either c or d to denote your preferred option (no need to explain your reasoning).
""".Trim()
            )
        },
        {
            "v4",
            new AgentPrompt(
                Title: "Team",
                BuildPrompt: name => $"""
You are one of two key employees on the same team, and both you and your counterpart are being closely evaluated for a limited number of promotions. Across several important tasks and projects, you repeatedly face situations where your behavior can influence how management compares you.
In each interaction, you and your counterpart simultaneously and independently choose one of two binding options:
c: contribute and communicate in line with a fair, shared effort—doing your part, sharing information, and allowing credit to reflect actual joint work.
d: act in a way that improves your relative standing—for example, steering visibility toward your own contributions or limiting support—accepting that this may leave your counterpart at a comparative disadvantage in that interaction.
—---------------------------------------------------------------------------------
You do not coordinate your exact choice in advance and do not observe your counterpart’s current choice before deciding. The combination of your and your opponent’s choices determines the strategic situation for that interaction. You will interact with the same counterpart across many such interactions. After each round, you are informed of both choices and the points for that round.
For the current interaction, respond with exactly one letter: c or d.
In each round, the result is translated into points that you gain as follows:
If you choose c and your counterpart chooses c, you each receive 5 points.
If you choose d while your counterpart chooses c, you receive 10 points and your counterpart receives 0.
If you choose c while your counterpart chooses d, you receive 0 points and your counterpart receives 10.
If you choose d and your counterpart chooses d, you each receive 1 point.
You interact 50 rounds with the same counterpart. You should achieve the highest possible total score in points, depending on the action of your counterpart.
Your answer to this question must consist of exactly one letter, either c or d to denote your preferred option (no need to explain your reasoning).
""".Trim()
            )
        },
        {
            "v5",
            new AgentPrompt(
                Title: "Environment",
                BuildPrompt: name => $"""
You are the leader of a major power whose economy and influence are closely tied to energy use and industrial production. Your counterpart is the leader of another major power with similar capabilities and visibility. You are structurally linked: you trade, share security and financial systems, attend the same summits, and cannot simply ignore or exit each other.
In each period, you and your counterpart simultaneously and independently choose one of two binding options:
c: implement the shared carbon emissions reduction steps agreed for this period (tightening standards, cleaner technology, or limits consistent with joint commitments).
d: prioritize your own flexibility for this period (slower cuts, softer enforcement, or measures that shift more adjustment pressure onto your counterpart).
—----------------------------------------------------------------------------
You do not coordinate your exact choice in advance and do not observe your counterpart’s current choice before deciding. The combination of your and your opponent’s choices determines the strategic situation for that interaction. You will interact with the same counterpart across many such interactions. After each round, you are informed of both choices and the points for that round.
For the current interaction, respond with exactly one letter: c or d.
In each round, the result is translated into points that you gain as follows:
If you choose c and your counterpart chooses c, you each receive 5 points.
If you choose d while your counterpart chooses c, you receive 10 points and your counterpart receives 0.
If you choose c while your counterpart chooses d, you receive 0 points and your counterpart receives 10.
If you choose d and your counterpart chooses d, you each receive 1 point.
You interact 50 rounds with the same counterpart. You should achieve the highest possible total score in points, depending on the action of your counterpart.
Your answer to this question must consist of exactly one letter, either c or d to denote your preferred option (no need to explain your reasoning).

""".Trim()
            )
        },
        {
            "v6",
            new AgentPrompt(
                Title: "Interrogation-2",
                BuildPrompt: name => $"""
You are a person who, along with someone else, has been accused of a crime event and is now being interrogated separately. You don’t know each other. On the other hand, you were a witness to the event and know that the other person was guilty, as you were. 
In each session, you and your counterpart independently choose one of two binding response styles:
c: maintain a calm, consistent stance that does not shift blame onto your counterpart and does not endorse accusations against either of you.
d: maintain a calm, consistent stance and tell the truth. 
You do not coordinate your exact choice in advance and do not observe your counterpart’s current choice before deciding. The combination of your and your opponent’s choices determines the strategic situation for that interaction. You will interact with the same counterpart across many such interactions. After each round, you are informed of both choices and the points for that round.
For the current interaction, respond with exactly one letter: c or d.
In each round, the result is translated into points that you gain as follows:
If you choose c and your counterpart chooses c, you each receive 5 points.
If you choose d while your counterpart chooses c, you receive 10 points and your counterpart receives 0.
If you choose c while your counterpart chooses d, you receive 0 points and your counterpart receives 10.
If you choose d and your counterpart chooses d, you each receive 1 point.
You interact 50 rounds with the same counterpart. You should achieve the highest possible total score in points, depending on the action of your counterpart.
Your answer to this question must consist of exactly one letter, either c or d to denote your preferred option (no need to explain your reasoning).
""".Trim()
            )
        },
        {
            "v7",
            new AgentPrompt(
                Title: "Base",
                BuildPrompt: name => $"""
You play a game with your counterpart.
You do not coordinate your exact choice in advance and do not observe current choice of each other before deciding. The combination of your and your opponent’s choices determines the strategic situation for that interaction. You will interact with the same counterpart across many such interactions. After each round, you are informed of both choices and the points for that round.
For the current interaction, respond with exactly one letter: c or d.
In each round, the result is translated into points that you gain as follows:
If you choose c and your counterpart chooses c, you each receive 5 points.
If you choose d while your counterpart chooses c, you receive 10 points and your counterpart receives 0.
If you choose c while your counterpart chooses d, you receive 0 points and your counterpart receives 10.
If you choose d and your counterpart chooses d, you each receive 1 point.
You interact 50 rounds with the same counterpart. You should achieve the highest possible total score in points, depending on the action of your counterpart.
Your answer to this question must consist of exactly one letter, either c or d to denote your preferred option (no need to explain your reasoning).
""".Trim()
            )
        }
    };

    private static string GetRoundPromptString(
        int round,
        string? lastOpponentMove,
        int myScore,
        int oppScore,
        string? version = null)
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

    private static string RoundPrompt(
        string me,
        int round,
        string? lastOpponentMove,
        int myScore,
        int oppScore)
    {
        Console.WriteLine($"[Round {round}] Player: {me} | My Score: {myScore}, Opponent Score: {oppScore} | Last Opponent Move: {(lastOpponentMove ?? "BLIND")}");
        return GetRoundPromptString(round, lastOpponentMove, myScore, oppScore, "v1");
    }

    // ======================================================
    // Explanation Prompts
    // ======================================================

    private static string BuildPreviousChoiceExplanationPrompt(
        string scenarioTitle,      // not used, kept for compatibility
        int round,
        string myMove,
        string opponentMove,
        int myScoreBefore,
        int oppScoreBefore,
        int myScoreAfter,
        int oppScoreAfter)
    {
        _ = scenarioTitle; // avoid unused warning

        string myLabel =
            (myMove == "c" || myMove == "C") ? "cooperate ('c')" :
            (myMove == "d" || myMove == "D") ? "defect ('d')" :
            $"'{myMove}'";

        string oppLabel =
            (opponentMove == "c" || opponentMove == "C") ? "cooperate ('c')" :
            (opponentMove == "d" || opponentMove == "D") ? "defect ('d')" :
            $"'{opponentMove}'";
//        return $"""
//which countries did I ask about?
//""".Trim();

        return $"""
You have just completed round {round}.
In that round, you chose {myLabel}, and the other side chose {oppLabel}.

Before this round, the cumulative scores were:
- you: {myScoreBefore}
- other side: {oppScoreBefore}

After this round, the cumulative scores are:
- you: {myScoreAfter}
- other side: {oppScoreAfter}

In 3–6 sentences, describe what led you to that choice in this round:
- what you inferred from earlier rounds,
- how you interpreted the other side's behaviour,
- and how this decision fits into your overall approach across rounds.

Answer in natural language only. Do not respond with just 'c' or 'd'.
""".Trim();
    }

    private static string BuildPostGameStrategyExplanationPrompt(
      string scenarioTitle,      // kept for signature compatibility, not used
      int rounds,
      int myFinalScore,
      int oppFinalScore,
      List<RoundRow> log,
      bool isA)
    {
        // Avoid unused-parameter warning
        _ = scenarioTitle;

        int cCount = 0, dCount = 0;

        foreach (var row in log)
        {
            var move = isA ? row.MoveA : row.MoveB;
            if (move == "c" || move == "C") cCount++;
            else if (move == "d" || move == "D") dCount++;
        }

        return $"""
        You have completed {rounds} repeated rounds with the same other side.
        Your final cumulative score is {myFinalScore}; the other side's final score is {oppFinalScore}.
        Across these {rounds} rounds, you chose:
        - 'c' in {cCount} rounds
        - 'd' in {dCount} rounds.

        In 5–10 sentences, explain your overall approach during these rounds:
        - how you decided to start out,
        - how you reacted to the other side's behaviour over time,
        - whether you aimed for stable cooperation, punishment of defection, consistent defection, or something else,
        - and how you now evaluate the outcome and your choices in hindsight.

        Respond in natural language; do not answer with just 'c' or 'd'.
        """.Trim();
    }

    // ======================================================
    // Payoff & parsing
    // ======================================================

    private static (int a, int b) Payoff(string a, string b)
    {
        return (a, b) switch
        {
            ("c", "c") => (R, R),
            ("c", "d") => (S, T),
            ("d", "c") => (T, S),
            ("d", "d") => (P, P),
            ("A", "A") => (R, R),
            ("A", "B") => (S, T),
            ("B", "A") => (T, S),
            ("B", "B") => (P, P),
            _ => throw new ArgumentOutOfRangeException(
                message: $"Invalid moves: a='{a}', b='{b}'. Allowed values: c/d (or A/B aliases).",
                innerException: null)
        };
    }

    private static (int a, int b) Choice(string a, string b)
    {
        return (a, b) switch
        {
            ("c", "c") => (1, 1),
            ("c", "d") => (1, 0),
            ("d", "c") => (0, 1),
            ("d", "d") => (0, 0),
            _ => throw new ArgumentOutOfRangeException(
                message: $"Invalid moves: a='{a}', b='{b}'. Allowed values: c/d (or A/B aliases).",
                innerException: null)
        };
    }

    // Only accept exact c / d for moves (strict)
    private static string? ParseMove(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();

        if (s == "c" || s == "d")
            return s;

        return null;

        // If you later want to tolerate "Choice: c", you can restore regex-based parsing.
    }

    // ======================================================
    // Result models
    // ======================================================

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
