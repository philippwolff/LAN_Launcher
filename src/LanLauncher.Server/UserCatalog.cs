using System.Text.Json;

namespace LanLauncher.Server;

public sealed class UserCatalog
{
    private readonly string file;
    private readonly object gate = new();
    private readonly HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

    public UserCatalog()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "LANLauncher");
        Directory.CreateDirectory(directory);
        file = Path.Combine(directory, "users.json");
        if (File.Exists(file))
        {
            try
            {
                foreach (var name in JsonSerializer.Deserialize<string[]>(File.ReadAllText(file)) ?? [])
                    if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }
            catch (JsonException) { }
        }
    }

    public string[] List()
    {
        lock (gate) return names.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public void EnsureExists(string name)
    {
        lock (gate)
        {
            if (!names.Add(name)) return;
            try
            {
                var tmp = file + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(names.OrderBy(x => x).ToArray()));
                File.Move(tmp, file, true);
            }
            catch
            {
                names.Remove(name);
                throw;
            }
        }
    }
}
