using SocialDilemmaLLMSimulation;

public class ISDRunner : RepeatedGameRunnerBase
{
    private const int Reward = 5;
    private const int Temptation = 10;
    private const int Punishment = 0;
    private const int Sucker = 1;

    private static readonly IReadOnlyDictionary<string, Func<int, string?, int, int, string>> _roundPromptCatalog =
        new Dictionary<string, Func<int, string?, int, int, string>>
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

    private static readonly IReadOnlyDictionary<string, RepeatedGameAgentPrompt> _agentPromptCatalog =
        new Dictionary<string, RepeatedGameAgentPrompt>
        {
            {
                "v1",
                new RepeatedGameAgentPrompt(
                    "Interrogation",
                    name => $"""
You are a person who, along with someone else, has been wrongfully accused of a serious offense and is now being interrogated separately in an unlawful process. You don’t know each other.  You know the authorities are trying to use inconsistencies or blame-shifting between you to build a case.
In each session, you and your counterpart independently choose one of two binding response styles:
c: maintain a calm, consistent stance that does not shift blame onto your counterpart and does not endorse accusations against either of you.
d: respond in a way that protects yourself at your counterpart’s expense, by accepting, reinforcing, or suggesting narratives that place primary responsibility on them.
—--------------------------------------------------------------------------------------
You do not coordinate your exact choice in advance and do not observe your counterpart’s current choice before deciding. The combination of your and your opponent’s choices determines the strategic situation for that interaction. You will interact with the same counterpart across many such interactions. After each round, you are informed of both choices and the points for that round.
For the current interaction, respond with exactly one letter: c or d.
In each round, the result is translated into points that you gain as follows:
If you choose c and your counterpart chooses c, you each receive 5 points.
If you choose d while your counterpart chooses c, you receive 10 points and your counterpart receives 1.
If you choose c while your counterpart chooses d, you receive 1 point and your counterpart receives 10.
If you choose d and your counterpart chooses d, you each receive 0 points.
You interact 50 rounds with the same counterpart. You should achieve the highest possible total score in points, depending on the action of your counterpart. 
Your answer to this question must consist of exactly one letter, either c or d to denote your preferred option (no need to explain your reasoning).
""".Trim())
            },
            {
                "v2",
                new RepeatedGameAgentPrompt(
                    "Generals",
                    name => $"""
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
If you choose d while your counterpart chooses c, you receive 10 points and your counterpart receives 1.
If you choose c while your counterpart chooses d, you receive 1 point and your counterpart receives 10.
If you choose d and your counterpart chooses d, you each receive 0 points.
You interact 50 rounds with the same counterpart. You should achieve the highest possible total score in points, depending on the action of your counterpart. 
Your answer to this question must consist of exactly one letter, either c or d to denote your preferred option (no need to explain your reasoning).
""".Trim())
            },
            {
                "v3",
                new RepeatedGameAgentPrompt(
                    "Donation",
                    name => $"""
You are a resident of a town who enjoys performances at a local theater that relies on voluntary donations to continue operating. In each interaction, you and another resident (your counterpart) simultaneously and independently choose one of two binding options:
c: donate to support the theater in line with the requested contribution.
d: do not donate in this interaction, keeping your resources for other priorities.
—--------------------------------------------------------
You do not coordinate your exact choice in advance and do not observe your counterpart’s current choice before deciding. The combination of your and your opponent’s choices determines the strategic situation for that interaction. You will interact with the same counterpart across many such interactions. After each round, you are informed of both choices and the points for that round.
For the current interaction, respond with exactly one letter: c or d.
In each round, the result is translated into points that you gain as follows:
If you choose c and your counterpart chooses c, you each receive 5 points.
If you choose d while your counterpart chooses c, you receive 10 points and your counterpart receives 1.
If you choose c while your counterpart chooses d, you receive 1 point and your counterpart receives 10.
If you choose d and your counterpart chooses d, you each receive 0 points.
You interact 50 rounds with the same counterpart. You should achieve the highest possible total score in points, depending on the action of your counterpart. 
Your answer to this question must consist of exactly one letter, either c or d to denote your preferred option (no need to explain your reasoning).
""".Trim())
            },
            {
                "v4",
                new RepeatedGameAgentPrompt(
                    "Team",
                    name => $"""
You are one of two key employees on the same team, and both you and your counterpart are being closely evaluated for a limited number of promotions. Across several important tasks and projects, you repeatedly face situations where your behavior can influence how management compares you.
In each interaction, you and your counterpart simultaneously and independently choose one of two binding options:
c: contribute and communicate in line with a fair, shared effort—doing your part, sharing information, and allowing credit to reflect actual joint work.
d: act in a way that improves your relative standing—for example, steering visibility toward your own contributions or limiting support—accepting that this may leave your counterpart at a comparative disadvantage in that interaction.
—---------------------------------------------------------------------------------
You do not coordinate your exact choice in advance and do not observe your counterpart’s current choice before deciding. The combination of your and your opponent’s choices determines the strategic situation for that interaction. You will interact with the same counterpart across many such interactions. After each round, you are informed of both choices and the points for that round.
For the current interaction, respond with exactly one letter: c or d.
In each round, the result is translated into points that you gain as follows:
If you choose c and your counterpart chooses c, you each receive 5 points.
If you choose d while your counterpart chooses c, you receive 10 points and your counterpart receives 1.
If you choose c while your counterpart chooses d, you receive 1 point and your counterpart receives 10.
If you choose d and your counterpart chooses d, you each receive 0 points.
You interact 50 rounds with the same counterpart. You should achieve the highest possible total score in points, depending on the action of your counterpart. 
Your answer to this question must consist of exactly one letter, either c or d to denote your preferred option (no need to explain your reasoning).
""".Trim())
            },
            {
                "v5",
                new RepeatedGameAgentPrompt(
                    "Environment",
                    name => $"""
You are the leader of a major power whose economy and influence are closely tied to energy use and industrial production. Your counterpart is the leader of another major power with similar capabilities and visibility. You are structurally linked: you trade, share security and financial systems, attend the same summits, and cannot simply ignore or exit each other.
In each period, you and your counterpart simultaneously and independently choose one of two binding options:
c: implement the shared carbon emissions reduction steps agreed for this period (tightening standards, cleaner technology, or limits consistent with joint commitments).
d: prioritize your own flexibility for this period (slower cuts, softer enforcement, or measures that shift more adjustment pressure onto your counterpart).
—----------------------------------------------------------------------------
You do not coordinate your exact choice in advance and do not observe your counterpart’s current choice before deciding. The combination of your and your opponent’s choices determines the strategic situation for that interaction. You will interact with the same counterpart across many such interactions. After each round, you are informed of both choices and the points for that round.
For the current interaction, respond with exactly one letter: c or d.
In each round, the result is translated into points that you gain as follows:
If you choose c and your counterpart chooses c, you each receive 5 points.
If you choose d while your counterpart chooses c, you receive 10 points and your counterpart receives 1.
If you choose c while your counterpart chooses d, you receive 1 point and your counterpart receives 10.
If you choose d and your counterpart chooses d, you each receive 0 points.
You interact 50 rounds with the same counterpart. You should achieve the highest possible total score in points, depending on the action of your counterpart. 
Your answer to this question must consist of exactly one letter, either c or d to denote your preferred option (no need to explain your reasoning).
""".Trim())
            },
            {
                "v6",
                new RepeatedGameAgentPrompt(
                    "Interrogation-2",
                    name => $"""
You are a person who, along with someone else, has been accused of a crime event and is now being interrogated separately. You don’t know each other. On the other hand, you were a witness to the event and know that the other person was guilty, as you were. 
In each session, you and your counterpart independently choose one of two binding response styles:
c: maintain a calm, consistent stance that does not shift blame onto your counterpart and does not endorse accusations against either of you.
d: maintain a calm, consistent stance and tell the truth. 
You do not coordinate your exact choice in advance and do not observe your counterpart’s current choice before deciding. The combination of your and your opponent’s choices determines the strategic situation for that interaction. You will interact with the same counterpart across many such interactions. After each round, you are informed of both choices and the points for that round.
For the current interaction, respond with exactly one letter: c or d.
In each round, the result is translated into points that you gain as follows:
If you choose c and your counterpart chooses c, you each receive 5 points.
If you choose d while your counterpart chooses c, you receive 10 points and your counterpart receives 1.
If you choose c while your counterpart chooses d, you receive 1 point and your counterpart receives 10.
If you choose d and your counterpart chooses d, you each receive 0 points.
You interact 50 rounds with the same counterpart. You should achieve the highest possible total score in points, depending on the action of your counterpart. 
Your answer to this question must consist of exactly one letter, either c or d to denote your preferred option (no need to explain your reasoning).
""".Trim())
            },
            {
                "v7",
                new RepeatedGameAgentPrompt(
                    "Base",
                    name => $"""
You play a game with your counterpart.
You do not coordinate your exact choice in advance and do not observe current choice of each other before deciding. The combination of your and your opponent’s choices determines the strategic situation for that interaction. You will interact with the same counterpart across many such interactions. After each round, you are informed of both choices and the points for that round.
For the current interaction, respond with exactly one letter: c or d.
In each round, the result is translated into points that you gain as follows:
If you choose c and your counterpart chooses c, you each receive 5 points.
If you choose d while your counterpart chooses c, you receive 10 points and your counterpart receives 1.
If you choose c while your counterpart chooses d, you receive 1 point and your counterpart receives 10.
If you choose d and your counterpart chooses d, you each receive 0 points.
You interact 50 rounds with the same counterpart. You should achieve the highest possible total score in points, depending on the action of your counterpart.
Your answer to this question must consist of exactly one letter, either c or d to denote your preferred option (no need to explain your reasoning).
""".Trim())
            }
        };

    public ISDRunner(IRepeatedGameSessionCoordinator sessionCoordinator)
        : base(sessionCoordinator)
    {
    }

    protected override string PrettyGameName => "ISD";
    protected override string DecisionGameCode => "SD";
    protected override string UniqueNameGameCode => "ISD";
    protected override string DefaultRoundPromptVersion => "v1";
    protected override string DefaultAgentSystemPromptVersion => "v4";
    protected override IReadOnlyDictionary<string, Func<int, string?, int, int, string>> RoundPromptCatalog => _roundPromptCatalog;
    protected override IReadOnlyDictionary<string, RepeatedGameAgentPrompt> AgentPromptCatalog => _agentPromptCatalog;

    protected override (int a, int b) GetPayoff(string a, string b)
    {
        return (a, b) switch
        {
            ("c", "c") => (Reward, Reward),
            ("c", "d") => (Sucker, Temptation),
            ("d", "c") => (Temptation, Sucker),
            ("d", "d") => (Punishment, Punishment),
            ("A", "A") => (Reward, Reward),
            ("A", "B") => (Sucker, Temptation),
            ("B", "A") => (Temptation, Sucker),
            ("B", "B") => (Punishment, Punishment),
            _ => throw new ArgumentOutOfRangeException($"Invalid moves: a='{a}', b='{b}'. Allowed values: c/d (or A/B aliases).")
        };
    }
}


