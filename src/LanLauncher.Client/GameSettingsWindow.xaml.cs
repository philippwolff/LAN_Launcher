using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using LanLauncher.Shared;

namespace LanLauncher.Client;

public partial class GameSettingsWindow : Window
{
    private readonly string installFolder;
    private readonly string gameName;
    private bool loadingCompatibility;
    private readonly Dictionary<string, string> compatibility = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> CompatibilityModes => compatibility;
    private static readonly (string Name, string Mode)[] Modes =
    {
        ("Windows-Standard (kein Kompatibilitätsmodus)", ""),
        ("Windows 8", "WIN8RTM"), ("Windows 7", "WIN7RTM"),
        ("Windows Vista SP2", "VISTASP2"), ("Windows Vista", "VISTARTM"),
        ("Windows XP SP3", "WINXPSP3"), ("Windows XP SP2", "WINXPSP2"),
        ("Windows 98 / ME", "WIN98"), ("Windows 95", "WIN95")
    };
    private readonly List<GameLaunchOption> extraExecutables = new();
    public IReadOnlyList<GameLaunchOption> AdditionalExecutables => extraExecutables;
    public string ExecutablePath { get; private set; } = "";

    public GameSettingsWindow(GameInfo game, LauncherSettings settings)
    {
        InitializeComponent();
        gameName = game.Name;
        GameTitle.Text = game.Name + " (Version " + game.Version + ")";
        installFolder = GameExecutableFinder.GetInstallFolder(settings.InstallDirectory, game.Name);
        InstallFolderText.Text = "Installationsordner: " + installFolder;
        if (settings.AdditionalExecutables.TryGetValue(game.Id, out var options))
            extraExecutables.AddRange(options);
        foreach (var item in settings.CompatibilityModes) compatibility[item.Key] = item.Value;
        CompatModeBox.ItemsSource = Modes.Select(x => x.Name).ToArray();
        RefreshExtras();
        if (settings.InstalledExecutables.TryGetValue(game.Id, out var existing))
            ExecutablePathBox.Text = existing;
        ExecutablePathBox.TextChanged += (_, _) => RefreshCompatibilityExecutables();
        RefreshCompatibilityExecutables();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var currentFolder = Path.GetDirectoryName(ExecutablePathBox.Text);
        var initial = currentFolder is not null && Directory.Exists(currentFolder)
            ? currentFolder
            : Directory.Exists(installFolder) ? installFolder : null;
        var picker = new OpenFileDialog
        {
            Title = "Spiel-EXE auswählen",
            Filter = "Anwendungen (*.exe)|*.exe",
            CheckFileExists = true
        };
        if (initial is not null) picker.InitialDirectory = initial;
        if (picker.ShowDialog(this) == true) ExecutablePathBox.Text = picker.FileName;
    }

    private void Detect_Click(object sender, RoutedEventArgs e)
    {
        var detected = GameExecutableFinder.FindBest(installFolder, gameName);
        if (detected is null)
        {
            MessageBox.Show(this, "Keine eindeutige Spiel-EXE im Installationsordner gefunden.",
                "Automatische Suche", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ExecutablePathBox.Text = detected;
    }

    private void RefreshExtras()
    {
        ExtraExecutablesList.ItemsSource = null;
        ExtraExecutablesList.ItemsSource = extraExecutables;
        RefreshCompatibilityExecutables();
    }

    private void RefreshCompatibilityExecutables()
    {
        if (CompatExeBox is null || CompatModeBox is null || ExecutablePathBox is null) return;
        loadingCompatibility = true;
        var old = (CompatExeBox.SelectedItem as GameLaunchOption)?.ExecutablePath;
        var options = new List<GameLaunchOption>();
        if (!string.IsNullOrWhiteSpace(ExecutablePathBox.Text))
            options.Add(new GameLaunchOption("Hauptspiel", ExecutablePathBox.Text.Trim()));
        options.AddRange(extraExecutables);
        CompatExeBox.ItemsSource = options;
        CompatExeBox.SelectedItem = options.FirstOrDefault(x => x.ExecutablePath == old) ?? options.FirstOrDefault();
        loadingCompatibility = false;
        CompatExe_SelectionChanged(null!, null!);
    }

    private void CompatExe_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loadingCompatibility || CompatModeBox is null) return;
        loadingCompatibility = true;
        var path = (CompatExeBox.SelectedItem as GameLaunchOption)?.ExecutablePath;
        var mode = path is not null && compatibility.TryGetValue(path, out var saved) ? saved : "";
        var index = Array.FindIndex(Modes, x => x.Mode == mode);
        CompatModeBox.SelectedIndex = index < 0 ? 0 : index;
        loadingCompatibility = false;
    }

    private void CompatMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loadingCompatibility || CompatExeBox?.SelectedItem is not GameLaunchOption option || CompatModeBox.SelectedIndex < 0) return;
        var mode = Modes[CompatModeBox.SelectedIndex].Mode;
        if (mode.Length == 0) compatibility.Remove(option.ExecutablePath);
        else compatibility[option.ExecutablePath] = mode;
    }

    private void AddExtra_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new OpenFileDialog
            {
                Title = "Zusätzliche Spiel-EXE auswählen",
                Filter = "Anwendungen (*.exe)|*.exe",
                CheckFileExists = true,
                InitialDirectory = Directory.Exists(installFolder) ? installFolder :
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            };
            if (picker.ShowDialog(this) != true) return;
            var selectedPath = picker.FileName;
            if (extraExecutables.Any(x => string.Equals(x.ExecutablePath, selectedPath, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(this, "Diese EXE ist bereits als Startoption vorhanden.");
                return;
            }

            var nameDialog = new GameLaunchNameWindow(Path.GetFileNameWithoutExtension(selectedPath))
            {
                Owner = this
            };
            if (nameDialog.ShowDialog() != true) return;
            extraExecutables.Add(new GameLaunchOption(nameDialog.OptionName, selectedPath));
            RefreshExtras();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Die zusätzliche EXE konnte nicht hinzugefügt werden:\\n" + ex.Message,
                "Fehler beim Hinzufügen", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RemoveExtra_Click(object sender, RoutedEventArgs e)
    {
        if (ExtraExecutablesList.SelectedItem is not GameLaunchOption option) return;
        extraExecutables.Remove(option);
        RefreshExtras();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var value = ExecutablePathBox.Text.Trim();
        if (!string.IsNullOrEmpty(value) && (!File.Exists(value) ||
            !string.Equals(Path.GetExtension(value), ".exe", StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "Bitte eine vorhandene EXE-Datei auswählen.",
                "Ungültige Spiel-EXE", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ExecutablePath = value;
        DialogResult = true;
    }
}
