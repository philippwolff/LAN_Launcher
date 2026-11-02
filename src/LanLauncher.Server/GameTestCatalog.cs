using System.Text.Json;

namespace LanLauncher.Server;

public sealed record GameTestInfo(bool Tested, string OperatingSystem);

public sealed class GameTestCatalog
{
    private readonly string file;
    private readonly object gate = new();
    private readonly Dictionary<string, GameTestInfo> values;

    public GameTestCatalog(IConfiguration configuration)
    {
        var root = Path.GetFullPath(configuration["StoragePath"] ?? Path.Combine(AppContext.BaseDirectory, "Storage"));
        Directory.CreateDirectory(root);
        file = Path.Combine(root, "game-tests.json");
        values = File.Exists(file)
            ? JsonSerializer.Deserialize<Dictionary<string, GameTestInfo>>(File.ReadAllText(file)) ?? new()
            : new();
    }

    public GameTestInfo Get(string id)
    {
        lock (gate) return values.GetValueOrDefault(id) ?? new GameTestInfo(false, "");
    }

    public void Set(string id, GameTestInfo info)
    {
        if (info.OperatingSystem?.Length > 120) throw new ArgumentException("Betriebssystemangabe ist zu lang.");
        if (info.Tested && string.IsNullOrWhiteSpace(info.OperatingSystem))
            throw new ArgumentException("Bitte das getestete Betriebssystem angeben.");
        lock (gate)
        {
            values[id] = new GameTestInfo(info.Tested, info.Tested ? info.OperatingSystem.Trim() : "");
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(values));
            File.Move(temp, file, true);
        }
    }
}
