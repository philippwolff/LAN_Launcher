using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace LanLauncher.Client;

public partial class InstallerOrderWindow : Window
{
    private sealed record InstallerEntry(string Path, string Display);
    private readonly ObservableCollection<InstallerEntry> entries = new();
    public string[] OrderedInstallers => entries.Select(x => x.Path).ToArray();

    public InstallerOrderWindow(string extractionFolder, IEnumerable<string> installers)
    {
        InitializeComponent();
        foreach (var path in installers)
            entries.Add(new InstallerEntry(path, Path.GetRelativePath(extractionFolder, path)));
        InstallersList.ItemsSource = entries;
        if (entries.Count > 0) InstallersList.SelectedIndex = 0;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) =>
        ThemeManager.Apply(LauncherSettings.Load().DarkMode);

    private void InstallersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpButton.IsEnabled = InstallersList.SelectedIndex > 0;
        DownButton.IsEnabled = InstallersList.SelectedIndex >= 0 &&
                               InstallersList.SelectedIndex < entries.Count - 1;
    }

    private void Move(int offset)
    {
        var index = InstallersList.SelectedIndex;
        var next = index + offset;
        if (index < 0 || next < 0 || next >= entries.Count) return;
        entries.Move(index, next);
        InstallersList.SelectedIndex = next;
        InstallersList.ScrollIntoView(entries[next]);
    }

    private void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void Down_Click(object sender, RoutedEventArgs e) => Move(1);
    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
