using System.Text.Json;
using LanLauncher.Shared;

namespace LanLauncher.Server;

public sealed class ModCatalog
{
    private readonly string file;
    private readonly object gate = new();
    private readonly Dictionary<string, ModConfiguration> values;

    public ModCatalog(IConfiguration configuration)
    {
        var root = Path.GetFullPath(configuration["StoragePath"] ?? Path.Combine(AppContext.BaseDirectory, "Storage"));
        Directory.CreateDirectory(root);
        file = Path.Combine(root, "mod-configurations.json");
        values = File.Exists(file)
            ? JsonSerializer.Deserialize<Dictionary<string, ModConfiguration>>(File.ReadAllText(file)) ?? new()
            : new();
    }

    public ModConfiguration? Get(string id)
    {
        lock (gate) return values.GetValueOrDefault(id);
    }

    public void Set(string id, ModConfiguration value)
    {
        if (string.IsNullOrWhiteSpace(value.BaseGameId) || string.IsNullOrWhiteSpace(value.LauncherExe) ||
            !SafeRelative(value.LauncherExe) ||
            (value.InstallerSteps ?? []).Any(step => !SafeRelative(step) ||
                !step.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) ||
            !value.LauncherExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Ungültige relative EXE-Pfade oder Hauptspiel-ID.");
        lock (gate)
        {
            values[id] = value;
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, file, true);
        }
    }

    private static bool SafeRelative(string path) =>
        !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path) &&
        !path.Split(['/', '\\']).Any(part => part is "" or "." or "..") &&
        path.IndexOfAny(Path.GetInvalidPathChars()) < 0;
}
