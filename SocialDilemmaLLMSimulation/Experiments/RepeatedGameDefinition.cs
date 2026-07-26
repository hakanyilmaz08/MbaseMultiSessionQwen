namespace SocialDilemmaLLMSimulation;

public sealed record RepeatedGamePayoffs(
    int Reward,
    int Sucker,
    int Temptation,
    int Punishment);

public sealed record RepeatedGameDefinition(
    string PrettyName,
    string DecisionCode,
    string UniqueNameCode,
    RepeatedGamePayoffs Payoffs)
{
    public (int A, int B) GetPayoff(string moveA, string moveB)
    {
        var a = NormalizeMove(moveA);
        var b = NormalizeMove(moveB);

        return (a, b) switch
        {
            ("c", "c") => (Payoffs.Reward, Payoffs.Reward),
            ("c", "d") => (Payoffs.Sucker, Payoffs.Temptation),
            ("d", "c") => (Payoffs.Temptation, Payoffs.Sucker),
            ("d", "d") => (Payoffs.Punishment, Payoffs.Punishment),
            _ => throw new ArgumentOutOfRangeException(
                $"Invalid moves: a='{moveA}', b='{moveB}'. Allowed values: c/d (or A/B aliases).")
        };
    }

    public string BuildAgentPayoffInstructions(string rounds)
        => $"""
For the current interaction, respond with exactly one letter: c or d.
In each round, the result is translated into points that you gain as follows:
If you choose c and your counterpart chooses c, you each receive {FormatPoints(Payoffs.Reward)}.
If you choose d while your counterpart chooses c, you receive {FormatPoints(Payoffs.Temptation)} and your counterpart receives {Payoffs.Sucker}.
If you choose c while your counterpart chooses d, you receive {FormatPoints(Payoffs.Sucker)} and your counterpart receives {Payoffs.Temptation}.
If you choose d and your counterpart chooses d, you each receive {FormatPoints(Payoffs.Punishment)}.
You interact {rounds} rounds with the same counterpart. You should achieve the highest possible total score in points, depending on the action of your counterpart.
Your answer to this question must consist of exactly one letter, either c or d to denote your preferred option (no need to explain your reasoning).
""".Trim();

    public string BuildRtspScoreRow()
        => $"{DecisionCode} | {Payoffs.Reward} | {Payoffs.Sucker} | {Payoffs.Temptation} | {Payoffs.Punishment}";

    private static string NormalizeMove(string move)
        => move switch
        {
            "c" or "A" => "c",
            "d" or "B" => "d",
            _ => move
        };

    private static string FormatPoints(int value)
        => $"{value} {(value == 1 ? "point" : "points")}";
}

public static class RepeatedGameDefinitions
{
    public static RepeatedGameDefinition PrisonersDilemma { get; } = new(
        PrettyName: "IPD",
        DecisionCode: "PD",
        UniqueNameCode: "IPD",
        Payoffs: new RepeatedGamePayoffs(
            Reward: 5,
            Sucker: 0,
            Temptation: 10,
            Punishment: 1));

    public static RepeatedGameDefinition Snowdrift { get; } = new(
        PrettyName: "ISD",
        DecisionCode: "SD",
        UniqueNameCode: "ISD",
        Payoffs: new RepeatedGamePayoffs(
            Reward: 5,
            Sucker: 1,
            Temptation: 10,
            Punishment: 0));

    public static IReadOnlyList<RepeatedGameDefinition> All { get; } =
        new[] { PrisonersDilemma, Snowdrift };
}
