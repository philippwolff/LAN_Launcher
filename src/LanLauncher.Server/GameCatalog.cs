using System.Security.Cryptography;
using System.Text.Json;
using LanLauncher.Shared;

namespace LanLauncher.Server;

public sealed class GameCatalog
{
    private readonly string root;
    private readonly string metadataPath;
    private readonly object gate = new();
    private readonly Dictionary<string, GameInfo> games;
    public string Inbox { get; }

    public GameCatalog(IConfiguration configuration)
    {
        root = Path.GetFullPath(configuration["StoragePath"] ?? Path.Combine(AppContext.BaseDirectory, "Storage"));
        Inbox = Path.Combine(root, "Inbox");
        Directory.CreateDirectory(Inbox);
        Directory.CreateDirectory(Path.Combine(root, "Packages"));
        metadataPath = Path.Combine(root, "games.json");
        games = File.Exists(metadataPath)
            ? JsonSerializer.Deserialize<Dictionary<string, GameInfo>>(File.ReadAllText(metadataPath)) ?? new()
            : new();
    }

    public GameInfo[] List() { lock (gate) return games.Values.OrderBy(g => g.Name).ToArray(); }

    public GameInfo Import(string sourceName, string name, string version, string? description)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version)) throw new ArgumentException("Name und Version sind erforderlich.");
        var file = Path.GetFileName(sourceName);
        if (file != sourceName || string.IsNullOrWhiteSpace(file) || !new[] { ".zip", ".exe" }.Contains(Path.GetExtension(file).ToLowerInvariant()))
            throw new ArgumentException("Nur ZIP- und EXE-Dateien im Inbox-Ordner werden unterstützt.");
        var source = Path.Combine(Inbox, file);
        if (!File.Exists(source)) throw new FileNotFoundException("Datei nicht im Inbox-Ordner gefunden.");
        var id = Guid.NewGuid().ToString("N");
        var target = Path.Combine(root, "Packages", id + Path.GetExtension(file).ToLowerInvariant());
        File.Copy(source, target);
        var game = new GameInfo(id, name.Trim(), version.Trim(), description);
        lock (gate) { games[id] = game; Save(); }
        return game;
    }

    public GameInfo Upload(Stream input, string originalFileName, string name, string version, string? description)
    {
        var ext = Path.GetExtension(originalFileName).ToLowerInvariant();
        if (ext is not (".zip" or ".exe")) throw new ArgumentException("Nur ZIP und EXE erlaubt.");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version)) throw new ArgumentException("Name und Version erforderlich.");
        var id = Guid.NewGuid().ToString("N");
        var target = Path.Combine(root, "Packages", id + ext);
        using (var output = File.Create(target)) input.CopyTo(output);
        var game = new GameInfo(id, name.Trim(), version.Trim(), description);
        lock (gate) { games[id] = game; Save(); }
        return game;
    }

    public (string Path, string Name)? Package(string id)
    {
        var folder = Path.Combine(root, "Packages");
        lock (gate) { if (!games.ContainsKey(id)) return null; }
        foreach (var ext in new[] { ".zip", ".exe" })
        {
            var path = Path.Combine(folder, id + ext);
            if (File.Exists(path)) return (path, id + ext);
        }
        return null;
    }

    public bool Remove(string id)
    {
        lock (gate)
        {
            if (!games.Remove(id)) return false;
            Save();
        }
        var package = Path.Combine(root, "Packages");
        foreach (var ext in new[] { ".zip", ".exe" })
        {
            var path = Path.Combine(package, id + ext);
            if (File.Exists(path)) File.Delete(path);
        }
        return true;
    }

    private void Save()
    {
        var temp = metadataPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(games, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, metadataPath, true);
    }
}
