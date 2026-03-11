// EchoBroker.cs
using SocialDilemmaLLMSimulation.Abstractions;
using SocialDilemmaLLMSimulation.Domain;

public sealed class EchoBroker : IModelBroker
{
    public Task<(string, (int, int), string?)> CompleteAsync(
        string model, string? system, IReadOnlyList<ChatMessage> messages,
        double temperature, double topP, string? kvHandle, CancellationToken ct = default)
    {
        var last = messages.LastOrDefault(m => m.Role == "user")?.Content ?? "(no input)";
        return Task.FromResult(($"[echo:{model}] {last}", (10, 5), kvHandle));
    }
}

