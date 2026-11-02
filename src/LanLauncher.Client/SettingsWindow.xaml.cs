using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace LanLauncher.Client;

public partial class SettingsWindow : Window
{
    public LauncherSettings Settings { get; private set; }

    public SettingsWindow(LauncherSettings current)
    {
        InitializeComponent();
        Settings = current;
        InstallPathBox.Text = current.InstallDirectory;
        DownloadPathBox.Text = current.DownloadDirectory;
        DarkModeCheckBox.IsChecked = current.DarkMode;
    }

    private void BrowseInstall_Click(object sender, RoutedEventArgs e) => Browse(InstallPathBox);
    private void BrowseDownload_Click(object sender, RoutedEventArgs e) => Browse(DownloadPathBox);

    private void Browse(System.Windows.Controls.TextBox box)
    {
        var dialog = new OpenFolderDialog { Title = "Ordner auswählen" };
        if (Directory.Exists(box.Text)) dialog.InitialDirectory = box.Text;
        if (dialog.ShowDialog(this) == true) box.Text = dialog.FolderName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var install = Path.GetFullPath(Environment.ExpandEnvironmentVariables(InstallPathBox.Text.Trim()));
            var download = Path.GetFullPath(Environment.ExpandEnvironmentVariables(DownloadPathBox.Text.Trim()));
            if (string.IsNullOrWhiteSpace(InstallPathBox.Text) || string.IsNullOrWhiteSpace(DownloadPathBox.Text))
                throw new ArgumentException("Beide Ordner müssen angegeben werden.");
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(download);
            var updated = new LauncherSettings
            {
                InstallDirectory = install,
                DarkMode = DarkModeCheckBox.IsChecked == true,
                DownloadDirectory = download,
                LastOpenedGameId = Settings.LastOpenedGameId,
                InstalledExecutables = new Dictionary<string, string>(Settings.InstalledExecutables),
                InstallerTypes = new Dictionary<string, string>(Settings.InstallerTypes),
                KnownGames = new Dictionary<string, LanLauncher.Shared.GameInfo>(Settings.KnownGames),
                DownloadedPackages = new Dictionary<string, string>(Settings.DownloadedPackages)
            };
            updated.Save();
            Settings = updated;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Einstellungen konnten nicht gespeichert werden:\n" + ex.Message,
                "Ungültiger Ordner", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
