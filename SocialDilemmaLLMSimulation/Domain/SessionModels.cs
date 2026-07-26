using System.Text.Json.Serialization;

namespace SocialDilemmaLLMSimulation.Domain;

public record Message(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

public record SessionMeta(
    string Sid,
    double Temperature,
    double TopP,
    string Model = "",
    bool TemperatureOverridden = false,
    bool TopPOverridden = false,
    string ProfileKey = "");
