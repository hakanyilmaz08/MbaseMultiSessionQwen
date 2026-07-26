using SocialDilemmaLLMSimulation;

public class IPDRunner : RepeatedGameRunnerBase
{
    public IPDRunner(IRepeatedGameSessionCoordinator sessionCoordinator)
        : base(sessionCoordinator)
    {
    }

    protected override RepeatedGameDefinition Definition
        => RepeatedGameDefinitions.PrisonersDilemma;
}
