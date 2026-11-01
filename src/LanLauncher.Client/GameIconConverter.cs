using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LanLauncher.Shared;

namespace LanLauncher.Client;

public sealed class GameIconConverter : IValueConverter
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiSmallIcon = 0x000000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        out ShFileInfo psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly Dictionary<string, ImageSource> cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object value, Type targetType, object parameter,
        System.Globalization.CultureInfo culture)
    {
        if (value is not GameInfo game) return null;
        var settings = LauncherSettings.Load();
        if (!settings.InstalledExecutables.TryGetValue(game.Id, out var path) || !File.Exists(path))
        {
            var folder = GameExecutableFinder.GetInstallFolder(settings.InstallDirectory, game.Name);
            path = GameExecutableFinder.FindBest(folder, game.Name);
        }
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        if (cache.TryGetValue(path, out var cached)) return cached;

        var handle = SHGetFileInfo(path, 0, out var info,
            (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiIcon | ShgfiSmallIcon);
        if (handle == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(info.hIcon,
                System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            cache[path] = image;
            return image;
        }
        finally { DestroyIcon(info.hIcon); }
    }

    public object ConvertBack(object value, Type targetType, object parameter,
        System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
