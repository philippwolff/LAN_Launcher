using System.IO;
using System.Text;

namespace LanLauncher.Client;

internal static class GameExecutableFinder
{
    public static string GetInstallFolder(string installRoot, string gameName) =>
        Path.Combine(installRoot, string.Concat(gameName.Select(ch =>
            Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)));

    public static string? FindBest(string folder, string gameName)
    {
        if (!Directory.Exists(folder)) return null;
        string[] files;
        try { files = Directory.GetFiles(folder, "*.exe", SearchOption.AllDirectories); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        var normalizedGame = Normalize(gameName);
        var candidates = files.Select(path => new
        {
            Path = path,
            Name = Normalize(Path.GetFileNameWithoutExtension(path)),
            FileName = Path.GetFileNameWithoutExtension(path),
            Relative = Path.GetRelativePath(folder, path)
        }).Where(x => !IsInstallerOrHelper(x.FileName, x.Relative)).ToArray();

        if (candidates.Length == 0) return null;
        if (candidates.Length == 1) return candidates[0].Path;

        var ranked = candidates.Select(x => new
        {
            x.Path,
            Score = (x.Name == normalizedGame ? 100 : 0)
                + (normalizedGame.Length >= 3 && x.Name.Contains(normalizedGame) ? 45 : 0)
                + (x.Relative.Count(ch => ch == Path.DirectorySeparatorChar) == 0 ? 12 : 0)
                + (x.Name is "0ad" or "pyrogenesis" && normalizedGame is "0ad" or "0adempiresascendant" ? 90 : 0)
                - (x.Relative.Contains("redist", StringComparison.OrdinalIgnoreCase) ? 100 : 0)
        }).OrderByDescending(x => x.Score).ToArray();

        // Avoid silently choosing the wrong EXE when several plausible candidates exist.
        return ranked[0].Score >= 40 && ranked[0].Score > ranked[1].Score
            ? ranked[0].Path : null;
    }

    private static string Normalize(string text) =>
        new string(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool IsInstallerOrHelper(string fileName, string relative)
    {
        var name = fileName.ToLowerInvariant();
        string[] excluded = ["setup", "install", "unins", "uninstall", "updater",
            "update", "crash", "report", "vc_redist", "vcredist", "dxsetup", "unitycrashhandler"];
        return excluded.Any(name.Contains) ||
            relative.Contains("redist", StringComparison.OrdinalIgnoreCase) ||
            relative.Contains("prerequisite", StringComparison.OrdinalIgnoreCase);
    }
}
