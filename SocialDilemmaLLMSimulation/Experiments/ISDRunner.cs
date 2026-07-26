using SocialDilemmaLLMSimulation;

public class ISDRunner : RepeatedGameRunnerBase
{
    public ISDRunner(IRepeatedGameSessionCoordinator sessionCoordinator)
        : base(sessionCoordinator)
    {
    }

    protected override RepeatedGameDefinition Definition
        => RepeatedGameDefinitions.Snowdrift;
}
