using System.Globalization;
using System.Windows.Data;
using LanLauncher.Shared;

namespace LanLauncher.Client;

public sealed class GameDisplayNameConverter : IValueConverter
{
    public static HashSet<string> LocalOnlyIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not GameInfo game) return "";
        return LocalOnlyIds.Contains(game.Id) ? game.Name + " (nur lokal verfügbar)" : game.Name;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
