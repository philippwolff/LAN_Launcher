using System.Text.Json;
using LanLauncher.Shared;

namespace LanLauncher.Server;

public sealed class GameTagCatalog
{
    private readonly string file;
    private readonly object gate = new();
    private readonly Dictionary<string, GameTags> tags;

    public GameTagCatalog(IConfiguration config)
    {
        var root = Path.GetFullPath(config["StoragePath"] ?? Path.Combine(AppContext.BaseDirectory, "Storage"));
        Directory.CreateDirectory(root);
        file = Path.Combine(root, "game-tags.json");
        tags = File.Exists(file)
            ? JsonSerializer.Deserialize<Dictionary<string, GameTags>>(File.ReadAllText(file)) ?? new()
            : new();
    }

    public GameTags Get(string id)
    {
        lock (gate) return tags.TryGetValue(id, out var value) ? value : new GameTags([], []);
    }

    public void Set(string id, GameTags value)
    {
        value = GameTagOptions.Validate(value);
        lock (gate)
        {
            tags[id] = value;
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(tags, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, file, true);
        }
    }

    public void Remove(string id)
    {
        lock (gate)
        {
            if (!tags.Remove(id)) return;
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(tags));
            File.Move(temp, file, true);
        }
    }
}
