using System.Windows;
using LanLauncher.Shared;

namespace LanLauncher.Client;

public partial class GameActionsWindow : Window
{
    public enum GameAction { None, Start, Install, LocalInstall, Download, Delete, Settings, Uninstall, Tags, Mod, Notice }
    public GameAction SelectedAction { get; private set; }

    public GameActionsWindow(GameInfo game)
    {
        InitializeComponent();
        GameTitle.Text = game.Name + " (Version " + game.Version + ")";
    }

    public void SetDownloaded(bool downloaded) => LocalInstallButton.IsEnabled = downloaded;

    public void SetInstalled(bool installed)
    {
        StartButton.IsEnabled = installed;
        UninstallButton.IsEnabled = installed;
    }

    public void ShowTags(GameTags tags)
    {
        TagsText.Text = "Spieler: " + (tags.PlayerCounts.Length == 0 ? "keine Angabe" : string.Join(", ", tags.PlayerCounts)) +
            "    |    Genres: " + (tags.Genres.Length == 0 ? "keine Angabe" : string.Join(", ", tags.Genres));
    }

    private void Choose(GameAction action)
    {
        SelectedAction = action;
        DialogResult = true;
    }
    private void Start_Click(object sender, RoutedEventArgs e) => Choose(GameAction.Start);
    private void Install_Click(object sender, RoutedEventArgs e) => Choose(GameAction.Install);
    private void LocalInstall_Click(object sender, RoutedEventArgs e) => Choose(GameAction.LocalInstall);
    private void Download_Click(object sender, RoutedEventArgs e) => Choose(GameAction.Download);
    private void Delete_Click(object sender, RoutedEventArgs e) => Choose(GameAction.Delete);
    private void Uninstall_Click(object sender, RoutedEventArgs e) => Choose(GameAction.Uninstall);
    private void Mod_Click(object sender, RoutedEventArgs e) => Choose(GameAction.Mod);
    private void Notice_Click(object sender, RoutedEventArgs e) => Choose(GameAction.Notice);
    private void Tags_Click(object sender, RoutedEventArgs e) => Choose(GameAction.Tags);
    private void Settings_Click(object sender, RoutedEventArgs e) => Choose(GameAction.Settings);
    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
