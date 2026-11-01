using System.Windows;
using LanLauncher.Shared;

namespace LanLauncher.Client;

public partial class GameActionsWindow : Window
{
    public enum GameAction { None, Start, Install, Download, Delete, Settings, Uninstall }
    public GameAction SelectedAction { get; private set; }

    public GameActionsWindow(GameInfo game)
    {
        InitializeComponent();
        GameTitle.Text = game.Name + " (Version " + game.Version + ")";
    }

    private void Choose(GameAction action)
    {
        SelectedAction = action;
        DialogResult = true;
    }
    private void Start_Click(object sender, RoutedEventArgs e) => Choose(GameAction.Start);
    private void Install_Click(object sender, RoutedEventArgs e) => Choose(GameAction.Install);
    private void Download_Click(object sender, RoutedEventArgs e) => Choose(GameAction.Download);
    private void Delete_Click(object sender, RoutedEventArgs e) => Choose(GameAction.Delete);
    private void Uninstall_Click(object sender, RoutedEventArgs e) => Choose(GameAction.Uninstall);
    private void Settings_Click(object sender, RoutedEventArgs e) => Choose(GameAction.Settings);
    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
