using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;
using LanLauncher.Client;
using LanLauncher.Client.Logging;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.Length > 0 ? args[0] : ".artifacts/diagnostics-ui");
        Directory.CreateDirectory(output);
        var log = new ClientLogStore(new(Path.Combine(output, "Logs-" + Guid.NewGuid().ToString("N"))));
        ClientLog.Configure(log);
        // Load the actual styles without running the launcher's Startup event or update checks.
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resourceXml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAppResources.xaml"));
        var dictionary = new XElement(ns + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            resourceXml.Root!.Element(ns + "Application.Resources")!.Elements());
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown,
            Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString()) };
        try
        {
            foreach (var severity in Enum.GetValues<LogSeverity>())
                log.Write(severity, "SMB", "Diagnosebeispiel: Dateiöffnung und ReadAsync; Windows-Kontext verfügbar.", operationId: "Download-123456");
            log.FlushAsync().GetAwaiter().GetResult();
            foreach (var dark in new[] { false, true })
            {
                log.FlushAsync().GetAwaiter().GetResult();
                var expected = log.ReadAsync().GetAwaiter().GetResult().Entries;
                var window = new SettingsWindow(new LauncherSettings { DarkMode = dark })
                {
                    Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                window.Show();
                Pump();
                window.UpdateLayout();
                var root = (FrameworkElement)window.Content;
                var panel = Find<DiagnosticsPanel>(root) ?? throw new Exception("Diagnosis panel missing");
                var list = (ListBox)panel.FindName("EntriesList");
                if (list.Items.Count != expected.Count)
                {
                    log.FlushAsync().GetAwaiter().GetResult();
                    var errors = log.ReadAsync().GetAwaiter().GetResult().Entries.Where(e => e.Severity == LogSeverity.ERROR).Select(e => e.ExceptionDetails);
                    throw new Exception("Automatic live loading failed: " + list.Items.Count +
                        "; status=" + ((TextBlock)panel.FindName("StorageStatus")).Text + "; same source=" + ReferenceEquals(panel.Source, log) + "\n" + string.Join("\n", errors));
                }
                var filter = (CheckBox)panel.FindName("DebugFilter");
                filter.IsChecked = false;
                filter.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                if (list.Items.Count != expected.Count(e => e.Severity != LogSeverity.DEBUG)) throw new Exception("DEBUG filter failed");
                filter.IsChecked = true;
                filter.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                if (list.Items.Count != expected.Count) throw new Exception("DEBUG filter restoration failed");
                log.Write(LogSeverity.INFO, "Client", "Live-Aktualisierung bei geöffneten Einstellungen geprüft.", operationId: "Download-live");
                log.FlushAsync().GetAwaiter().GetResult();
                Pump(1100);
                if (list.Items.Count != expected.Count + 1) throw new Exception("Live timer did not append a new persisted entry");
                root.Measure(new Size(root.ActualWidth, root.ActualHeight));
                root.Arrange(new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                root.UpdateLayout();
                Pump();
                if (!Enumerable.Range(0, list.Items.Count).Select(i => list.ItemContainerGenerator.ContainerFromIndex(i))
                    .OfType<ListBoxItem>().Any(item => Find<TextBlock>(item)?.Text is { Length: > 0 }))
                    throw new Exception("Log row template/binding did not render");
                if (list.ActualHeight < 100 || root.ActualHeight < 500) throw new Exception("Scrollable log area collapsed");
                if (list.Background is not SolidColorBrush brush || (dark ? brush.Color.R > 100 : brush.Color.R < 200))
                    throw new Exception("Log list theme contrast incorrect");
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                {
                    drawing.DrawRectangle(window.Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                    drawing.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                }
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(Path.Combine(output, dark ? "settings-dark.png" : "settings-light.png"))) encoder.Save(file);
                Console.WriteLine("PASS: " + (dark ? "Dark" : "Light") + " settings: live entries, level filters, usable log area, theme contrast, screenshot.");
                window.Close();
                VerifyOpeningWindow(dark, output);
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { app.Shutdown(); log.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static void Pump(int milliseconds = 300)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void VerifyOpeningWindow(bool dark, string output)
    {
        var window = new TransferProgressWindow("Download: Testpaket")
        {
            Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        window.Show();
        Pump();
        ThemeManager.Apply(dark);
        using var release = new ManualResetEventSlim();
        using var preparation = NasPreparation.AcquireAsync(@"\\ui-open-" + Guid.NewGuid().ToString("N") + @"\Games",
            false, "WPF opening test", default).GetAwaiter().GetResult();
        var calls = 0;
        var opening = preparation.RunOpeningAsync(() =>
        {
            Interlocked.Increment(ref calls); release.Wait(); return new MemoryStream(new byte[] { 1, 2, 3 });
        }, stream => stream.Dispose(), window.Cancellation, window.SetOpeningWait, TimeSpan.FromMilliseconds(40));
        MemoryStream? result = null;
        try
        {
            var keepWaiting = (Button)window.FindName("ContinueWaitingButton");
            var cancel = (Button)window.FindName("CancelButton");
            var details = (TextBlock)window.FindName("TransferDetails");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (keepWaiting.Visibility != Visibility.Visible && clock.Elapsed < TimeSpan.FromSeconds(5)) Pump(25);
            if (keepWaiting.Visibility != Visibility.Visible || !cancel.IsEnabled || details.Text != SmbFileOpening.WaitingMessage)
                throw new Exception("Slow open waiting UI missing");
            SavePreview(window, Path.Combine(output, dark ? "opening-wait-dark.png" : "opening-wait-light.png"));
            keepWaiting.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump(600);
            if (calls != 1 || opening.IsCompleted || keepWaiting.Visibility != Visibility.Collapsed)
                throw new Exception("Continue waiting restarted/completed the opening instead of awaiting the same task");
            release.Set();
            clock.Restart();
            while (!opening.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(5)) Pump(25);
            if (!opening.IsCompleted) throw new Exception("Open did not complete after native release");
            result = opening.GetAwaiter().GetResult();
            Pump(50);
            if (keepWaiting.Visibility != Visibility.Collapsed) throw new Exception("Wait notice was not cleared on success");
            window.Update(1024 * 1024, 4 * 1024 * 1024, true);
            if (!details.Text.Contains($"Download: {0.0:0.00} MB/s") || !details.Text.Contains("Restzeit:"))
                throw new Exception("Preparation time polluted initial transfer speed/ETA");
            Pump(600);
            window.Update(2 * 1024 * 1024, 4 * 1024 * 1024, true);
            if (!details.Text.Contains("50,0") && !details.Text.Contains("50.0")) throw new Exception("Transfer percentage missing");
            if (TransferProgressWindow.FormatRemaining(1024, 2048, 1024) != "00:01" ||
                TransferProgressWindow.FormatRemaining(2048, 2048, 1024) != "00:00") throw new Exception("ETA calculation incorrect");
            SavePreview(window, Path.Combine(output, dark ? "opening-progress-dark.png" : "opening-progress-light.png"));
            window.SetOpeningWait(true); // A stale notification must not replace active progress.
            if (keepWaiting.Visibility != Visibility.Collapsed) throw new Exception("Stale wait notification replaced transfer progress");
            window.CancelTransfer();
            if (!window.Cancellation.IsCancellationRequested || cancel.IsEnabled) throw new Exception("Cancel button/token state incorrect");
            Console.WriteLine("PASS: " + (dark ? "Dark" : "Light") + " SMB waiting: same operation, continue/cancel controls, automatic transition, progress/speed/ETA, previews.");
        }
        finally
        {
            window.CancelTransfer();
            release.Set();
            result?.Dispose();
            window.CloseAfterTransfer();
        }
    }

    private static void SavePreview(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(root.ActualWidth, root.ActualHeight));
        root.Arrange(new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(window.Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            drawing.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static T? Find<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (Find<T>(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }
}
