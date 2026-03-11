using SocialDilemmaLLMSimulation.Domain;

namespace SocialDilemmaLLMSimulation.Abstractions;

public interface IModelBroker
{
    Task<(string text, (int PromptTokens, int CompletionTokens) usage, string? kvHandle)> CompleteAsync(
        string model, string? system, IReadOnlyList<ChatMessage> messages,
        double temperature, double topP, string? kvHandle,
        CancellationToken ct = default);
}



