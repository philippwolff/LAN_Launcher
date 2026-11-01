using System.Text.Json;

namespace LanLauncher.Server;

public sealed class GameStartNoticeCatalog
{
    private readonly string file;
    private readonly object gate = new();
    private readonly Dictionary<string, string> notices;

    public GameStartNoticeCatalog(IConfiguration config)
    {
        var root = Path.GetFullPath(config["StoragePath"] ?? Path.Combine(AppContext.BaseDirectory, "Storage"));
        Directory.CreateDirectory(root);
        file = Path.Combine(root, "game-start-notices.json");
        notices = File.Exists(file)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? new()
            : new();
    }

    public string Get(string id)
    {
        lock (gate) return notices.GetValueOrDefault(id) ?? "";
    }

    public void Set(string id, string text)
    {
        if (text.Length > 2000) throw new ArgumentException("Maximal 2000 Zeichen erlaubt.");
        lock (gate)
        {
            if (string.IsNullOrWhiteSpace(text)) notices.Remove(id);
            else notices[id] = text.Trim();
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(notices, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, file, true);
        }
    }
}
