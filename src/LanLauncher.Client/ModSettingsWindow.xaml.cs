using System.Windows;
using LanLauncher.Shared;

namespace LanLauncher.Client;

public partial class ModSettingsWindow : Window
{
    public ModConfiguration? Configuration { get; private set; }

    public ModSettingsWindow(GameInfo[] games, ModConfiguration? existing)
    {
        InitializeComponent();
        BaseGameBox.ItemsSource = games;
        BaseGameBox.SelectedItem = games.FirstOrDefault(g => g.Id == existing?.BaseGameId);
        if (existing is not null)
        {
            LauncherBox.Text = existing.LauncherExe;
            StepsBox.Text = string.Join(Environment.NewLine, existing.InstallerSteps);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (BaseGameBox.SelectedItem is not GameInfo game ||
            string.IsNullOrWhiteSpace(LauncherBox.Text))
        {
            MessageBox.Show(this, "Bitte Hauptspiel und Start-EXE auswählen.");
            return;
        }
        Configuration = new ModConfiguration(game.Id, LauncherBox.Text.Trim(),
            StepsBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        DialogResult = true;
    }
}
