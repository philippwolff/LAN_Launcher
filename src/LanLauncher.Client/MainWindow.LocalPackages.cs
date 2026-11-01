using System.IO;
using LanLauncher.Shared;

namespace LanLauncher.Client;

public partial class MainWindow
{
    private bool TryGetDownloadedPackage(GameInfo game, out string package)
    {
        package = "";
        if (!settings.DownloadedPackages.TryGetValue(game.Id, out var path) ||
            string.IsNullOrWhiteSpace(path) ||
            Path.GetExtension(path).ToLowerInvariant() is not (".zip" or ".exe") ||
            !File.Exists(path))
            return false;
        package = path;
        return true;
    }
}
