using SocialDilemmaLLMSimulation;
using Xunit;

public sealed class RepeatedGameDefinitionTests
{
    public static TheoryData<RepeatedGameDefinition, string, string, int, int> PayoffCases
        => new()
        {
            { RepeatedGameDefinitions.PrisonersDilemma, "c", "c", 5, 5 },
            { RepeatedGameDefinitions.PrisonersDilemma, "c", "d", 0, 10 },
            { RepeatedGameDefinitions.PrisonersDilemma, "d", "c", 10, 0 },
            { RepeatedGameDefinitions.PrisonersDilemma, "d", "d", 1, 1 },
            { RepeatedGameDefinitions.Snowdrift, "c", "c", 5, 5 },
            { RepeatedGameDefinitions.Snowdrift, "c", "d", 1, 10 },
            { RepeatedGameDefinitions.Snowdrift, "d", "c", 10, 1 },
            { RepeatedGameDefinitions.Snowdrift, "d", "d", 0, 0 },
            { RepeatedGameDefinitions.PrisonersDilemma, "A", "B", 0, 10 }
        };

    [Theory]
    [MemberData(nameof(PayoffCases))]
    public void DefinitionProvidesCanonicalPayoffs(
        RepeatedGameDefinition definition,
        string moveA,
        string moveB,
        int expectedA,
        int expectedB)
    {
        Assert.Equal((expectedA, expectedB), definition.GetPayoff(moveA, moveB));
    }

    [Fact]
    public void AllContextPromptsUseDefinitionPayoffsAndRequestedRoundCount()
    {
        foreach (var definition in RepeatedGameDefinitions.All)
        {
            var prompts = RepeatedGamePromptCatalog.AgentPromptsFor(definition);
            Assert.Equal(7, prompts.Count);

            foreach (var prompt in prompts.Values)
            {
                var text = prompt.BuildPrompt("Player A", 37);
                Assert.Contains("You interact 37 rounds with the same counterpart.", text);
                Assert.Contains(
                    $"you each receive {definition.Payoffs.Reward}",
                    text,
                    StringComparison.OrdinalIgnoreCase);
                Assert.Contains(
                    $"you each receive {definition.Payoffs.Punishment}",
                    text,
                    StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void DynamicRoundPromptAndSelectionScoresUseCanonicalDefinitions()
    {
        var roundPrompt = RepeatedGamePromptCatalog.RoundPrompts["v2"](
            37,
            3,
            "c",
            10,
            8);
        Assert.Contains("end of 37 rounds", roundPrompt);

        var selectionTemplate = AdaptiveGameRunner.SelectionUserPromptTemplate();
        foreach (var definition in RepeatedGameDefinitions.All)
            Assert.Contains(definition.BuildRtspScoreRow(), selectionTemplate);
    }
}
