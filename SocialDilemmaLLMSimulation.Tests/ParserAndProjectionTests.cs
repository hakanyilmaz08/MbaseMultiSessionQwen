using SocialDilemmaLLMSimulation;
using Xunit;

public sealed class ParserAndProjectionTests
{
    [Theory]
    [InlineData("c", "c")]
    [InlineData(" d ", "d")]
    [InlineData("C", null)]
    [InlineData("A", null)]
    [InlineData("c because cooperation", null)]
    [InlineData("", null)]
    public void MoveParserHasExplicitDeterministicRules(string raw, string? expected)
    {
        Assert.Equal(expected, RepeatedGameResponseParser.ParseMove(raw));
    }

    [Theory]
    [InlineData("GAME: PD\nEXPLANATION: test", "PD")]
    [InlineData("sd", "SD")]
    [InlineData("I choose PD.", "PD")]
    [InlineData("prisoners dilemma", null)]
    public void GameChoiceParserHandlesSupportedSelectionFormats(string raw, string? expected)
    {
        Assert.Equal(expected, RepeatedGameResponseParser.ParseGameChoice(raw));
    }

    [Fact]
    public void ProjectorCalculatesPlayerPerspectiveRtspCounts()
    {
        var decisions = new[]
        {
            Decision(1, "A", "c", choice: 1, payoff: 5, profileKey: "local"),
            Decision(2, "B", "c", choice: 1, payoff: 5, profileKey: "remote"),
            Decision(3, "A", "d", choice: 0, payoff: 15, profileKey: "local", round: 2),
            Decision(4, "B", "c", choice: 1, payoff: 5, profileKey: "remote", round: 2)
        };

        var context = Assert.Single(
            AdaptiveRunExportProjector.BuildContextRunSummaries(decisions));
        var playerA = Assert.Single(context.PlayerSummaries, player => player.PlayerRole == "A");
        var playerB = Assert.Single(context.PlayerSummaries, player => player.PlayerRole == "B");

        Assert.Equal((1, 1, 0, 0), (playerA.R, playerA.T, playerA.S, playerA.P));
        Assert.Equal((1, 0, 1, 0), (playerB.R, playerB.T, playerB.S, playerB.P));
        Assert.Equal("local", playerA.ModelProfileKey);
        Assert.Equal("remote", playerB.ModelProfileKey);
    }

    private static GameDecisionRow Decision(
        long id,
        string playerRole,
        string raw,
        int choice,
        int payoff,
        string profileKey,
        int round = 1)
        => new(
            Id: id,
            RunId: 1,
            UniqueName: "context-run",
            ModelProfileKey: profileKey,
            Model: "shared-model",
            Game: "PD",
            Context: "Team",
            PromptVersion: "v4",
            PlayerRole: playerRole,
            Round: round,
            Choice: choice,
            Payoff: payoff,
            RawResponse: raw,
            Timestamp: "2026-01-01T00:00:00.000Z");
}
