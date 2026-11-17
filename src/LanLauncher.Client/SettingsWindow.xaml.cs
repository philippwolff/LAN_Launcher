using System.IO;
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using LanLauncher.Client.Logging;

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
        AdminModeCheckBox.IsChecked = current.RunAsAdministrator;
        LogBudgetBox.Text = Math.Clamp(current.LogMaxTotalMiB, 2, 200).ToString();
        Loaded += (_, _) => ThemeManager.Apply(current.DarkMode);
        UpdateRestartVisibility();
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
        if (TrySaveSettings()) DialogResult = true;
    }

    private bool TrySaveSettings()
    {
        try
        {
            if (!int.TryParse(LogBudgetBox.Text, out var logBudget) || logBudget is < 2 or > 200)
                throw new ArgumentException("Der Protokollspeicher muss zwischen 2 und 200 MiB liegen.");
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
                LogMaxTotalMiB = logBudget,
                RunAsAdministrator = AdminModeCheckBox.IsChecked == true,
                DownloadDirectory = download,
                LastOpenedGameId = Settings.LastOpenedGameId,
                LastSuccessfulServer = Settings.LastSuccessfulServer,
                HiddenGameIds = new HashSet<string>(Settings.HiddenGameIds, StringComparer.OrdinalIgnoreCase),
                InstalledExecutables = new Dictionary<string, string>(Settings.InstalledExecutables),
                AdditionalExecutables = Settings.AdditionalExecutables.ToDictionary(x => x.Key, x => x.Value.ToList()),
                CompatibilityModes = new Dictionary<string, string>(Settings.CompatibilityModes),
                InstallerTypes = new Dictionary<string, string>(Settings.InstallerTypes),
                KnownGames = new Dictionary<string, LanLauncher.Shared.GameInfo>(Settings.KnownGames),
                DownloadedPackages = new Dictionary<string, string>(Settings.DownloadedPackages)
            };
            updated.Save();
            ClientLog.Store.SetBudget(logBudget * 1024L * 1024);
            Settings = updated;
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Einstellungen konnten nicht gespeichert werden:\n" + ex.Message,
                "Ungültiger Ordner", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void AdminMode_Changed(object sender, RoutedEventArgs e) => UpdateRestartVisibility();

    private void UpdateRestartVisibility()
    {
        if (RestartButton is not null)
            RestartButton.Visibility = AdminModeCheckBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (!TrySaveSettings()) return;
        try
        {
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("Der Pfad zum Launcher ist nicht verfügbar.");
            Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            });
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            MessageBox.Show(this, "Die Administratoranfrage wurde abgebrochen. Die Einstellungen wurden gespeichert.",
                "Neustart abgebrochen", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Der Neustart war nicht möglich. Die Einstellungen wurden gespeichert.\\n" + ex.Message,
                "Neustart fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
