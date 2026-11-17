using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using LanLauncher.Client.Logging;
using Microsoft.Win32;

namespace LanLauncher.Client;

public partial class DiagnosticsPanel : UserControl
{
    private readonly ObservableCollection<LogEntry> entries = new();
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ICollectionView view;
    private long version = -1;
    private bool reading;
    internal ILogSource Source { get; set; } = ClientLog.Store;

    public DiagnosticsPanel()
    {
        InitializeComponent();
        view = CollectionViewSource.GetDefaultView(entries);
        view.Filter = item => item is LogEntry entry && Enabled(entry.Severity);
        EntriesList.ItemsSource = view;
        refresh.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) =>
        {
            SourceNameText.Text = Source.Name + "-Protokoll";
            OpenFolderButton.IsEnabled = Source is ClientLogStore;
            refresh.Start(); await RefreshAsync();
        };
        Unloaded += (_, _) => refresh.Stop();
    }

    private bool Enabled(LogSeverity severity) => severity switch
    {
        LogSeverity.DEBUG => DebugFilter.IsChecked == true,
        LogSeverity.INFO => InfoFilter.IsChecked == true,
        LogSeverity.WARN => WarnFilter.IsChecked == true,
        _ => ErrorFilter.IsChecked == true
    };

    private void Filter_Click(object sender, RoutedEventArgs e) => view?.Refresh();

    private async Task RefreshAsync()
    {
        if (reading) return;
        reading = true;
        try
        {
            var snapshot = await Source.ReadAsync();
            StorageStatus.Text = snapshot.StorageStatus + $" · Ansicht: {snapshot.Entries.Count} letzte Einträge";
            if (snapshot.Version == version) return;
            var ids = snapshot.Entries.Select(e => e.Id).ToHashSet();
            while (entries.Count > 0 && !ids.Contains(entries[0].Id)) entries.RemoveAt(0);
            var existing = entries.Select(e => e.Id).ToHashSet();
            foreach (var entry in snapshot.Entries) if (existing.Add(entry.Id)) entries.Add(entry);
            if (FollowNewest.IsChecked == true && view.Cast<LogEntry>().LastOrDefault() is { } newest)
                EntriesList.ScrollIntoView(newest);
            version = snapshot.Version;
        }
        catch (Exception error)
        {
            ClientLog.Write(LogSeverity.ERROR, "Diagnose", "Aktualisierung der Protokollanzeige fehlgeschlagen.", error);
            StorageStatus.Text = "Protokollanzeige nicht verfügbar: " + error.GetType().Name;
        }
        finally { reading = false; }
    }

    private async Task<string> ExportTextAsync()
    {
        var snapshot = await Source.ReadAsync();
        var filtered = snapshot.Entries.Where(e => Enabled(e.Severity)).ToArray();
        var anonymize = AnonymizeExport.IsChecked == true;
        return await Task.Run(() => LogFormat.Export(filtered, anonymize));
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(await ExportTextAsync()); StorageStatus.Text = "Gefilterte Einträge kopiert."; }
        catch (Exception error) { ShowError(error); }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = $"LAN-Launcher-Client-{DateTime.Now:yyyyMMdd-HHmmss}.log", Filter = "Protokoll (*.log)|*.log|Text (*.txt)|*.txt" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var text = await ExportTextAsync();
            await File.WriteAllTextAsync(dialog.FileName, text);
            StorageStatus.Text = "Gefilterte Einträge exportiert.";
        }
        catch (Exception error) { ShowError(error); }
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Source is not ClientLogStore store) return;
            await store.FlushAsync();
            var path = store.DirectoryPath;
            await Task.Run(() => Directory.CreateDirectory(path));
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception error) { ShowError(error); }
    }

    private void ShowError(Exception error)
    {
        ClientLog.Write(LogSeverity.ERROR, "Diagnose", "Kopieren, Exportieren oder Öffnen fehlgeschlagen.", error);
        MessageBox.Show(Window.GetWindow(this), "Aktion fehlgeschlagen: " + error.GetType().Name,
            "Diagnose & Protokolle", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
