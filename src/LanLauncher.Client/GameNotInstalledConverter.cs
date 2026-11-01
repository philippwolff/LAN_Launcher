using System.IO;
using System.Globalization;
using System.Windows.Data;
using LanLauncher.Shared;

namespace LanLauncher.Client;

public sealed class GameNotInstalledConverter : IValueConverter
{
    public static HashSet<string> ServerGameIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not GameInfo game || !ServerGameIds.Contains(game.Id)) return false;
        var settings = LauncherSettings.Load();
        if (settings.InstalledExecutables.TryGetValue(game.Id, out var exe) && File.Exists(exe))
            return false;
        var folder = GameExecutableFinder.GetInstallFolder(settings.InstallDirectory, game.Name);
        return GameExecutableFinder.FindBest(folder, game.Name) is null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
