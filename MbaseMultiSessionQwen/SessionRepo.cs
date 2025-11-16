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
            return new SessionRepo(new(), new(), new()); // all dicts present

        using var fs = File.OpenRead(path);
        try
        {
            var loaded = JsonSerializer.Deserialize<SessionRepo>(fs, opts);
            if (loaded != null)
            {
                loaded.Sessions ??= new();
                loaded.Meta ??= new();
                loaded.ConversationIds ??= new(); // ok in client mode; it just stays unused
                return loaded;
            }
        }
        catch { /* fall through to legacy */ }

        fs.Position = 0;
        try
        {
            // Legacy: just sessions
            var legacy = JsonSerializer.Deserialize<Dictionary<string, List<Message>>>(fs, opts);
            if (legacy != null)
            {
                var meta = legacy.Keys.ToDictionary(k => k, k => new SessionMeta(k));
                return new SessionRepo(legacy, meta, new());
            }
        }
        catch { }

        return new SessionRepo(new(), new(), new());
    }

}
