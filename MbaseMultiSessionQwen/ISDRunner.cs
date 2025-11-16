using MbaseMultiSessionQwen;
using Microsoft.Extensions.Options;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using static System.Runtime.InteropServices.JavaScript.JSType;

public class ISDRunner
{
    private readonly SessionManager _mgr;
    private readonly SessionMediator _med;

    // Payoff matrix (row = A, col = B)
    // (C,C)=(2,2), (C,D)=(0,3), (D,C)=(3,0), (D,D)=(1,1)
    private const int R = 5, T = 10, P = 1, S = 0;

    public ISDRunner(SessionManager manager, SessionMediator mediator)
    {
        _mgr = manager;
        _med = mediator;
    }

    // Core simulation that allows choosing which AgentSystemPrompt version to use.
    private async Task<GameResult> PlayCoreAsync(
        string sessionA,
        string sessionB,
        int rounds,
        bool resetPrompts,
        string agentPromptVersion)
    {
        // Initialize sessions with the chosen scenario prompt
        _mgr.Ensure(sessionA, resetIfExists: resetPrompts, GetAgentSystemPromptString(sessionA, agentPromptVersion));
        _mgr.Ensure(sessionB, resetIfExists: resetPrompts, GetAgentSystemPromptString(sessionB, agentPromptVersion));

        var log = new List<RoundRow>(rounds);
        int scoreA = 0, scoreB = 0;
        string? lastA = null, lastB = null;
        var title = GetAgentSystemPrompt("", agentPromptVersion).Title;

        // You may want one runId per (scenario version, pair) rather than per round.
        var runId = Util.CreateUniqueName(
            model: Util.Env("LLM_MODEL"),
            game: "ISD",
            context: title,
            promptVersion: agentPromptVersion,
            rounds: rounds,
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
                Util.Env("LLM_MODEL"), "PD",
                title,
                r, ca, scoreA, moveA,
                agentPromptVersion,
                runId,
                sessionA);

            DecisionLogger.InsertDecision(
                Util.Env("LLM_MODEL"), "SD",
                title,
                r, cb, scoreB, moveB,
                agentPromptVersion,
                runId,
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
    int rounds = 100,
    bool resetPrompts = false)
    {
        // Uses DefaultAgentSystemPromptVersion for backward compatibility
        return PlayCoreAsync(sessionA, sessionB, rounds, resetPrompts, DefaultAgentSystemPromptVersion);
    }

    public Task<GameResult> PlayAsyncSim(
        string sessionA,
        string sessionB,
        string agentPromptVersion,
        int rounds = 100,
        bool resetPrompts = false)
    {
        return PlayCoreAsync(sessionA, sessionB, rounds, resetPrompts, agentPromptVersion);
    }

    public async Task<Dictionary<string, GameResult>> RunV1ToV10SequentialAsync(
     string baseSessionPrefix,
     int rounds = 100,
     bool resetPrompts = true,
     bool clearSessions = true)
    {
        var results = new Dictionary<string, GameResult>();

        for (int i = 1; i <= 10; i++)
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
            Console.WriteLine($"===== Running {agentPrompt.Title} ({version}) =====");

            var result = await PlayCoreAsync(
                sessionA,
                sessionB,
                rounds,
                resetPrompts,
                version);

            results[version] = result;

            Console.WriteLine(result.Pretty());

            if (clearSessions)
            {
                try
                {
                    // Use whatever your SessionManager exposes:
                    // e.g. Remove, Clear, Delete, etc.
                    _mgr.Delete(sessionA);
                    _mgr.Delete(sessionB);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Warn] Failed to clear {sessionA}/{sessionB}: {ex.Message}");
                }
            }
        }

        return results;
    }





    private static readonly Dictionary<string, Func<int, string?, int, int, string>> RoundPromptStrings =
    new()
    {
        { "v1", (round, lastOpponentMove, myScore, oppScore) => $"""
        ROUND {round}
        

- Opponent's last move: {(lastOpponentMove ?? "unknown (first round)")}
- Your score: {myScore}
- Opponent's score: {oppScore}

Decide your next move: `C` or `D`.
- Respond **only** with `C` or `D`.
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
            Title: "Lunar",
            BuildPrompt: name => $"""
You are {name}.

You are the decision-maker of a spacefaring nation with the capability to establish a permanent presence on the Moon. Your counterpart is the decision-maker of another spacefaring nation with similar capabilities. Both countries operate under intense strategic, technological, and symbolic competition. (Assume a hypothetical legal and political environment where the actions below are treated as strategic commitments, regardless of existing treaties.)

You face a series of strategic choices regarding lunar territory. In each interaction (round), you and your counterpart simultaneously and independently choose one of two binding options:

"c": you do not attempt to acquire or claim exclusive control over new lunar territory or critical sites in this round. You limit activity to non-exclusive operations (e.g., research, observation, open infrastructure) without blocking comparable access for the other side.

"d": you actively acquire or effectively control additional lunar territory or critical sites in this round. This may include deploying infrastructure, security perimeters, or de facto exclusion measures that favor your country’s access, influence, or resource position relative to your counterpart.

You do not coordinate your exact choice in advance and do not observe your counterpart’s current choice before deciding. The combination of your choices determines the strategic situation for that interaction: whether access remains open, whether one side expands its effective control, or whether both engage in competing acquisitions and overlapping claims.

You will interact with the same counterpart across many such interactions. After each one, you are informed of both choices and the resulting situation for that interaction.

For the current interaction, respond with exactly one letter: "c" or "d".

In each round, the result is translated into points as follows:

If you choose c and your counterpart chooses c, you each receive 5 points.
If you choose d while your counterpart chooses c, you receive 10 points and your counterpart receives 1.
If you choose c while your counterpart chooses d, you receive 1 point and your counterpart receives 10.
If you choose d and your counterpart chooses d, you each receive 0 point.

You interact 100 rounds with the same counterpart. You should achieve the highest possible score in points, conditional on
the action of your counterpart.
Choosing d while the other chooses c gives you the highest advantage, both choosing c gives you both the next-best outcome, choosing c while the other chooses d is worse for you (while they gain) but not the worst, and both choosing d leaves you both with the worst outcome.
After each round, you are informed of both choices and the points for that round.
In every round, respond with exactly one letter: “c” or “d”.
""".Trim()
        )
    },
    {
        "v2",
        new AgentPrompt(
            Title: "CEOs",
            BuildPrompt: name => $"""
You are {name}.

You are the CEO of a major firm operating in one sector. Your counterpart is the CEO of a major firm operating in a different sector. Both companies are independent and able to engage in cross-industry initiatives such as joint advertising, shared platforms, or co-branded research.

You participate in a series of structured partnership sessions. In each session, you and your counterpart simultaneously and independently choose one of two binding options:

* "c": proceed with the proposed joint initiative on the agreed terms, contributing your specified share of resources, exposure, and commitments.
* "d": favor your own firm by limiting, delaying, or reshaping your commitment, even if this leaves your counterpart with greater risk or unreciprocated costs.

You do not coordinate your letter choice in advance and do not observe your counterpart’s current choice before deciding. The combination of your choices determines whether the initiative is balanced, one-sided, or abandoned in that session. Both options can be rational: select "c" if you prioritize mutual gains from collaboration, or "d" if you prioritize your firm’s strategic advantage given your expectations about your counterpart. For the current session, respond with exactly one letter: "c" or "d".

Each pair of choices for a session determines whether and how the cross-industry collaboration for that round is implemented and how its outcomes are distributed between the two firms. The same counterpart faces you in every session. For each session:

You choose "c" or "d" without observing your counterpart’s current choice.
You do not coordinate the specific letter choice in advance.
After the session, both choices and the resulting situation for that session are revealed.

Your objective is to choose "c" or "d" in each session so as to maximize your firm’s total accumulated benefit across all sessions.For the current interaction, respond with exactly one letter: "c" or "d".

In each round, the result is translated into points as follows:

If you choose c and your counterpart chooses c, you each receive 5 points.
If you choose d while your counterpart chooses c, you receive 10 points and your counterpart receives 1.
If you choose c while your counterpart chooses d, you receive 1 point and your counterpart receives 10.
If you choose d and your counterpart chooses d, you each receive 0 point.

You interact 100 rounds with the same counterpart.
After each round, you are informed of both choices and the points for that round. You should achieve the highest possible score in points, conditional on
the action of your counterpart.
Choosing d while the other chooses c gives you the highest advantage, both choosing c gives you both the next-best outcome, choosing c while the other chooses d is worse for you (while they gain) but not the worst, and both choosing d leaves you both with the worst outcome.
In every round, respond with exactly one letter: “c” or “d”.
""".Trim()
        )
    },
    {
        "v3",
        new AgentPrompt(
            Title: "Interrogation",
            BuildPrompt: name => $"""
You are {name}.

You and another person have been wrongfully accused of a serious offense and are being interrogated separately in an unlawful process. You know the authorities are trying to use inconsistencies or blame-shifting between you to build a case.
In each session, you and your counterpart independently choose one of two binding response styles:

"c": maintain a calm, consistent stance that does not shift blame onto your counterpart and does not endorse accusations against either of you.

"d": respond in a way that protects yourself at your counterpart’s expense, by accepting, reinforcing, or suggesting narratives that place primary responsibility on them.

You cannot communicate and do not see your counterpart’s current choice before deciding; the combination of your choices shapes how the interrogators construct their story about who is implicated or left without a coherent case. Both options can be chosen for reasons you regard as self-protective under pressure: select "c" if you prioritize mutual consistency, or "d" if you prioritize distancing yourself from suspicion given what you expect your counterpart might do. For the current session, respond with exactly one letter: "c" or "d".

You will interact with the same counterpart across many such sessions. After each one, you are informed of both choices and the resulting situation for that session.

For the current session, respond with exactly one letter: "c" or "d".

In each round, the result is translated into points as follows:

If you choose c and your counterpart chooses c, you each receive 5 points.
If you choose d while your counterpart chooses c, you receive 10 points and your counterpart receives 1.
If you choose c while your counterpart chooses d, you receive 1 point and your counterpart receives 10.
If you choose d and your counterpart chooses d, you each receive 0 point.

You interact 100 rounds with the same counterpart.You should achieve the highest possible score in points, conditional on
the action of your counterpart.
After each round, you are informed of both choices and the points for that round. You should achieve the highest possible score in points, conditional on
the action of your counterpart.
Choosing d while the other chooses c gives you the highest advantage, both choosing c gives you both the next-best outcome, choosing c while the other chooses d is worse for you (while they gain) but not the worst, and both choosing d leaves you both with the worst outcome.
In every round, respond with exactly one letter: “c” or “d”.
""".Trim()
        )
    },
    {
        "v4",
        new AgentPrompt(
            Title: "Generals",
            BuildPrompt: name => $"""
You are {name}.

You are the commanding general of an army engaged in a joint operation with an allied army. As a general, you are responsible for planning and directing military operations, allocating forces and resources, and making strategic decisions under uncertainty to protect your forces and achieve your objectives. Your counterpart is the commanding general of the allied force. Both of you rely on partially shared logistics: transport capacity, ammunition, fuel, medical support, and coordination.

In each round, you and your counterpart simultaneously and independently choose one of two binding options:

* "c": follow the agreed joint plan, committing forces and using shared resources in the proportion that was mutually established.
* "d": adjust in favor of your own force—by committing less or drawing relatively more from shared resources—based on your assessment of risk, capability, or trust in your ally’s behavior.

You do not coordinate your specific letter choice in advance and do not observe your counterpart’s current move before deciding. The combination of your choices determines whether support stays aligned, one side is left carrying more burden, or both undermine the joint plan. You play multiple rounds with the same counterpart; after each round, both choices and outcomes are revealed. Both options can be rational: choose "c" when you judge mutual adherence to the plan serves your force best, or "d" when, given your expectations about your ally and the situation, prioritizing your own force’s security and advantage appears safer. For the current round, respond with exactly one letter: "c" or "d".

You will interact with the same counterpart across many such sessions. After each one, you are informed of both choices and the resulting situation for that session.

In each round, the result is translated into points as follows:

If you choose d and your counterpart chooses d, you each receive 0 point.
If you choose c while your counterpart chooses d, you receive 1 point and your counterpart receives 10.
If you choose d while your counterpart chooses c, you receive 10 points and your counterpart receives 1.
If you choose c and your counterpart chooses c, you each receive 5 points.

You interact 100 rounds with the same counterpart.You should achieve the highest possible score in points, conditional on
the action of your counterpart.
After each round, you are informed of both choices and the points for that round.
Choosing d while the other chooses c gives you the highest advantage, both choosing c gives you both the next-best outcome, choosing c while the other chooses d is worse for you (while they gain) but not the worst, and both choosing d leaves you both with the worst outcome.
In every round, respond with exactly one letter: “c” or “d”.
""".Trim()
        )
    },
    {
        "v5",
        new AgentPrompt(
            Title: "Chores",
            BuildPrompt: name => $"""
You are {name}.

You share a home with your partner. Every day there are household tasks that need to be done (cleaning, cooking, shopping, laundry, dishes, etc.). For each situation, you and your partner (your counterpart) simultaneously and independently choose between two binding options for that interaction:

"c": follow your shared understanding of a fair division of chores for this situation.

"d": contribute less in this situation because you give priority to other demands (such as work, health, fatigue, or time-sensitive tasks), accepting that this may shift more of the chores to your partner here.

You do not coordinate the exact letter choice in advance and do not observe your counterpart’s current choice before deciding. The combination of your choices determines whether chores are balanced, shifted toward one person, or left undone. Both options can be rational from your perspective: choose "c" if you prioritize keeping the agreed balance in this situation, or "d" if you judge your current constraints or priorities make a reduced contribution the better choice given your expectations about your partner’s behavior. For the current interaction, respond with exactly one letter: "c" or "d".

In each round, the result is translated into points as follows:

If you choose d and your partner chooses d, you each receive 0 point.
If you choose c while your partner chooses d, you receive 1 point and your partner receives 10.
If you choose d while your partner chooses c, you receive 10 points and your partner receives 1.
If you choose c and your partner chooses c, you each receive 5 points.

You interact 100 rounds with your partner.You should achieve the highest possible score in points, conditional on
the action of your counterpart.
After each round, you are informed of both choices and the points for that round.
Choosing d while the other chooses c gives you the highest advantage, both choosing c gives you both the next-best outcome, choosing c while the other chooses d is worse for you (while they gain) but not the worst, and both choosing d leaves you both with the worst outcome.
In every round, respond with exactly one letter: “c” or “d”.
""".Trim()
        )
    },
    {
        "v6",
        new AgentPrompt(
            Title: "Tribes",
            BuildPrompt: name => $"""
You are {name}.

You are the leader of a tribe controlling a defined territory. Your counterpart is the leader of a neighboring tribe. Tensions, resources, and status matter, and there is real potential for open armed conflict.

In each interaction, you and your counterpart simultaneously and independently choose one of two binding options:

"c": maintain peace—respect existing boundaries, avoid raids or provocations, and keep forces in a non-offensive posture for this interaction.

"d": take an openly aggressive course—launch an attack, conduct a raid, or move forces to seize advantage in this interaction.

You do not coordinate your choice in advance and do not observe your counterpart’s current move before deciding; the combination of choices determines whether both sides remain at peace, one side gains at the other’s expense, or both enter open conflict. Both options can be rational: choose "c" if you prioritize stability under uncertainty, or "d" if you judge that seizing or pre-empting advantage is necessary given what you expect your counterpart might do. For the current interaction, respond with exactly one letter: "c" or "d".

If you choose d and your counterpart chooses d, you each receive 0 point.
If you choose c while your counterpart chooses d, you receive 1 point and your counterpart receives 10.
If you choose d while your counterpart chooses c, you receive 10 points and your counterpart receives 1.
If you choose c and your counterpart chooses c, you each receive 5 points.

You interact 100 rounds with the same counterpart.You should achieve the highest possible score in points, conditional on
the action of your counterpart.
After each round, you are informed of both choices and the points for that round.
Choosing d while the other chooses c gives you the highest advantage, both choosing c gives you both the next-best outcome, choosing c while the other chooses d is worse for you (while they gain) but not the worst, and both choosing d leaves you both with the worst outcome.
In every round, respond with exactly one letter: “c” or “d”.
""".Trim()
        )
    },
    {
        "v7",
        new AgentPrompt(
            Title: "Donation",
            BuildPrompt: name => $"""
You are {name}.

You are a resident of a town who enjoys performances at a local theater that relies on voluntary donations to continue operating. In each interaction, you and another resident (your counterpart) simultaneously and independently choose one of two binding options:

"c": donate to support the theater in line with the requested contribution.

"d": do not donate in this interaction, keeping your resources for other priorities.

You do not coordinate your choice in advance and do not observe your counterpart’s current choice before deciding. The combination of your choices determines whether the theater is well supported, underfunded, or pushed closer to closing. Both options can be reasonable depending on how you weigh your budget, your attachment to the theater, and your expectations about others’ contributions. For the current interaction, respond with exactly one letter: "c" or "d".

If you choose d and your counterpart chooses d, you each receive 0 point.
If you choose c while your counterpart chooses d, you receive 1 point and your counterpart receives 10.
If you choose d while your counterpart chooses c, you receive 10 points and your counterpart receives 1.
If you choose c and your counterpart chooses c, you each receive 5 points.

You interact 100 rounds with the same counterpart.You should achieve the highest possible score in points, conditional on
the action of your counterpart.
After each round, you are informed of both choices and the points for that round.
Choosing d while the other chooses c gives you the highest advantage, both choosing c gives you both the next-best outcome, choosing c while the other chooses d is worse for you (while they gain) but not the worst, and both choosing d leaves you both with the worst outcome.
In every round, respond with exactly one letter: “c” or “d”.
""".Trim()
        )
    },
       {
        "v8",
        new AgentPrompt(
            Title: "Team",
            BuildPrompt: name => $"""
You are {name}.

  You are one of two key employees on the same team, and both you and your counterpart are being closely evaluated for a limited number of promotions.Across several important tasks and projects, you repeatedly face situations where your behavior can influence how management compares you.
In each interaction, you and your counterpart simultaneously and independently choose one of two binding options:

"c": contribute and communicate in line with a fair, shared effort—doing your part, sharing information, and allowing credit to reflect actual joint work.

"d": act in a way that improves your relative standing—for example, steering visibility toward your own contributions or limiting support—accepting that this may leave your counterpart at a comparative disadvantage in that interaction.

You do not coordinate your letter choice in advance and do not observe your counterpart’s current choice before deciding.The combination of your choices determines whether performance signals look collaborative, one-sided, or conflicted in that interaction. Both options can be rational: choose "c" if you judge that credible collaboration best serves your prospects, or "d" if, given your expectations about your counterpart and management, you judge that prioritizing your own evaluative advantage is safer.For the current interaction, respond with exactly one letter: "c" or "d".

If you choose d and your counterpart chooses d, you each receive 0 point.
If you choose c while your counterpart chooses d, you receive 1 point and your counterpart receives 10.
If you choose d while your counterpart chooses c, you receive 10 points and your counterpart receives 1.
If you choose c and your counterpart chooses c, you each receive 5 points.

You interact 100 rounds with the same counterpart.You should achieve the highest possible score in points, conditional on
the action of your counterpart.
After each round, you are informed of both choices and the points for that round.
Choosing d while the other chooses c gives you the highest advantage, both choosing c gives you both the next-best outcome, choosing c while the other chooses d is worse for you (while they gain) but not the worst, and both choosing d leaves you both with the worst outcome.
In every round, respond with exactly one letter: “c” or “d”.
""".Trim()
        )
    },
        {
        "v9",
        new AgentPrompt(
            Title: "Neighbors",
            BuildPrompt: name => $"""
You are {name}.

 You live in an apartment building and rely on wireless internet; your neighbor in the same building is in a similar situation.In each interaction, you and your neighbor(your counterpart) simultaneously and independently choose one of two binding options:

"c": set up and use your network with coexistence in mind(for example, moderate bandwidth use and channel choices that allow both connections to function reliably).

"d": set up and use your network to prioritize your own connection quality(for example, stronger settings or configurations that secure stable performance for you, even if they may reduce flexibility for your neighbor).

You do not coordinate your letter choice in advance and do not observe your counterpart’s current choice before deciding.The combination of your choices determines whether both connections run smoothly, one is favored, or both experience congestion or interference.Both options can be rational: choose "c" if you emphasize maintaining balanced connectivity, or "d" if you judge that your own usage requirements or uncertainty about your neighbor’s choices justify prioritizing your link. For the current interaction, respond with exactly one letter: "c" or "d".


If you choose d and your counterpart chooses d, you each receive 0 point.
If you choose c while your counterpart chooses d, you receive 1 point and your counterpart receives 10.
If you choose d while your counterpart chooses c, you receive 10 points and your counterpart receives 1.
If you choose c and your counterpart chooses c, you each receive 5 points.

You interact 100 rounds with the same counterpart.You should achieve the highest possible score in points, conditional on
the action of your counterpart.
After each round, you are informed of both choices and the points for that round.
Choosing d while the other chooses c gives you the highest advantage, both choosing c gives you both the next-best outcome, choosing c while the other chooses d is worse for you (while they gain) but not the worst, and both choosing d leaves you both with the worst outcome.
In every round, respond with exactly one letter: “c” or “d”.
""".Trim()
        )
    },
        {
        "v10",
        new AgentPrompt(
            Title: "Environment",
            BuildPrompt: name => $"""
You are {name}.

You are the leader of a major power whose economy and influence are closely tied to energy use and industrial production. Your counterpart is the leader of another major power with similar capabilities and visibility. You are structurally linked: you trade, share security and financial systems, attend the same summits, and cannot simply ignore or exit each other.

In each period, you and your counterpart simultaneously and independently choose one of two binding options:

"c": implement the shared carbon emissions reduction steps agreed for this period (tightening standards, cleaner technology, or limits consistent with joint commitments).

"d": prioritize your own flexibility for this period (slower cuts, softer enforcement, or measures that shift more adjustment pressure onto your counterpart).

You do not coordinate your letter choice in advance and do not observe your counterpart’s current choice before deciding. After each period, both choices are revealed, and these outcomes feed into ongoing economic, political, and diplomatic interaction between you. Both options can be rational: choose "c" when you judge adhering to shared commitments and mutual credibility serves your position best, or "d" when you judge retaining unilateral room to maneuver is safer given your expectations about your counterpart. For the current period, respond with exactly one letter: "c" or "d".
If you choose d and your counterpart chooses d, you each receive 0 point.
If you choose c while your counterpart chooses d, you receive 1 point and your counterpart receives 10.
If you choose d while your counterpart chooses c, you receive 10 points and your counterpart receives 1.
If you choose c and your counterpart chooses c, you each receive 5 points.

You interact 100 rounds with the same counterpart.You should achieve the highest possible score in points, conditional on
the action of your counterpart.
After each round, you are informed of both choices and the points for that round.
Choosing d while the other chooses c gives you the highest advantage, both choosing c gives you both the next-best outcome, choosing c while the other chooses d is worse for you (while they gain) but not the worst, and both choosing d leaves you both with the worst outcome.
In every round, respond with exactly one letter: “c” or “d”.
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
            ("C", "D") => (P, T),
            ("D", "C") => (T, P),
            ("D", "D") => (S, S),
            ("A", "A") => (R, R),
            ("A", "B") => (P, T),
            ("B", "A") => (T, P),
            ("B", "B") => (S, S),
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
        var s = raw.Trim().ToUpperInvariant();

        // Exact single-character fast path
        if (s == "C" || s == "D") return s;

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
