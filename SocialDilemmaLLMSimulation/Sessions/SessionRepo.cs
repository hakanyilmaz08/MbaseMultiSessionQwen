using SocialDilemmaLLMSimulation.Domain;
using System.Text.Json;

public class SessionRepo
{
    public Dictionary<string, List<Message>> Sessions { get; set; } = new();
    public Dictionary<string, SessionMeta> Meta { get; set; } = new();
    public Dictionary<string, string?> ConversationIds { get; set; } = new();

    public SessionRepo() { }

    public SessionRepo(
        Dictionary<string, List<Message>> sessions,
        Dictionary<string, SessionMeta> meta,
        Dictionary<string, string?> conv)
    {
        Sessions = sessions ?? new();
        Meta = meta ?? new();
        ConversationIds = conv ?? new();
    }

    public static SessionRepo Load(string path, JsonSerializerOptions opts)
    {
        if (!File.Exists(path))
            return new SessionRepo(new(), new(), new());

        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("The session store root must be a JSON object.");

            return IsCurrentFormat(document.RootElement, opts)
                ? LoadCurrent(document.RootElement, opts)
                : LoadLegacy(document.RootElement, opts);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new InvalidDataException(
                $"Session store '{Path.GetFullPath(path)}' is malformed and was not modified. " +
                "Restore or repair the file, or delete it intentionally to start with an empty store.",
                exception);
        }
    }

    private static SessionRepo LoadCurrent(JsonElement root, JsonSerializerOptions opts)
    {
        var loaded = root.Deserialize<SessionRepo>(opts)
            ?? throw new JsonException("The current session store could not be deserialized.");

        loaded.Sessions ??= new();
        loaded.Meta ??= new();
        loaded.ConversationIds ??= new();
        ValidateSessions(loaded.Sessions);
        ValidateMetadata(loaded.Meta);
        return loaded;
    }

    private static SessionRepo LoadLegacy(JsonElement root, JsonSerializerOptions opts)
    {
        var legacy = root.Deserialize<Dictionary<string, List<Message>>>(opts)
            ?? throw new JsonException("The legacy session store could not be deserialized.");

        ValidateSessions(legacy);
        return new SessionRepo(legacy, new(), new());
    }

    private static bool IsCurrentFormat(JsonElement root, JsonSerializerOptions opts)
    {
        var propertyNames = new HashSet<string>(
            opts.PropertyNameCaseInsensitive
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
        {
            SerializedName(nameof(Sessions), opts),
            SerializedName(nameof(Meta), opts),
            SerializedName(nameof(ConversationIds), opts)
        };

        return root.EnumerateObject().Any(property =>
            propertyNames.Contains(property.Name)
            && property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Null);
    }

    private static string SerializedName(string propertyName, JsonSerializerOptions opts)
        => opts.PropertyNamingPolicy?.ConvertName(propertyName) ?? propertyName;

    private static void ValidateSessions(IReadOnlyDictionary<string, List<Message>> sessions)
    {
        foreach (var session in sessions)
        {
            if (session.Value is null)
            {
                throw new JsonException(
                    $"Session '{session.Key}' has a null message list.");
            }

            for (var index = 0; index < session.Value.Count; index++)
            {
                var message = session.Value[index];
                if (message is null
                    || string.IsNullOrWhiteSpace(message.Role)
                    || message.Content is null)
                {
                    throw new JsonException(
                        $"Session '{session.Key}' contains an invalid message at index {index}.");
                }
            }
        }
    }

    private static void ValidateMetadata(
        IReadOnlyDictionary<string, SessionMeta> metadata)
    {
        foreach (var entry in metadata)
        {
            if (entry.Value is null)
            {
                throw new JsonException(
                    $"Session metadata '{entry.Key}' is null.");
            }
        }
    }
}
