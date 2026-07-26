namespace SocialDilemmaLLMSimulation.Domain;

public sealed record ChatMessage(string Role, string Content, DateTimeOffset Ts);

public sealed class SessionState
{
    public required string SessionId { get; init; }               // immutable id
    public required string ProfileKey { get; set; }
    public required string Model { get; set; }                     // e.g., "Qwen2.5-7B-Instruct"
    public string? SystemPrompt { get; set; }                      // mutable
    public required double Temperature { get; set; }
    public required double TopP { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow; // **set**, not init

    public string? KvCacheHandle { get; set; }                     // optional: engine KV handle

    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }

    // Optional TTL if you want expiry/GC later
    public TimeSpan? Ttl { get; set; }
}
