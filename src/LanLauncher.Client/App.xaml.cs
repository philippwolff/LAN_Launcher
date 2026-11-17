using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.IO;
using LanLauncher.Client.Logging;

namespace LanLauncher.Client;

public partial class App : Application
{
    private void Application_Startup(object sender, StartupEventArgs e)
    {
        var settings = LauncherSettings.Load();
        ClientLog.Configure(new ClientLogStore(new LogStoreOptions(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LAN_Launcher", "Logs"),
            Math.Clamp(settings.LogMaxTotalMiB, 2, 200) * 1024L * 1024)));
        ClientLog.Write(LogSeverity.INFO, "Client", "Client-Start. " + NasWindowsContext.Capture());
        DispatcherUnhandledException += (_, args) => ClientLog.Write(LogSeverity.ERROR, "Client", "Unbehandelte UI-Exception.", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => ClientLog.Write(LogSeverity.ERROR, "Client", "Unbehandelte Prozess-Exception.", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => ClientLog.Write(LogSeverity.ERROR, "Client", "Unbeobachtete Task-Exception.", args.Exception);
        var isElevated = false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            isElevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception) { /* Normal startup if elevation cannot be determined. */ }

        if (settings.RunAsAdministrator && !isElevated)
        {
            try
            {
                var executable = Environment.ProcessPath
                    ?? throw new InvalidOperationException("Pfad zum Launcher konnte nicht ermittelt werden.");
                Process.Start(new ProcessStartInfo(executable)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = AppContext.BaseDirectory
                });
                ClientLog.Write(LogSeverity.INFO, "Client", "Erhöhter Neustart angefordert.");
                Shutdown();
                return;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                MessageBox.Show("Die Administratoranfrage wurde abgebrochen. Der Launcher wird ohne Administratorrechte gestartet.",
                    "Administratorrechte", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Der Start als Administrator war nicht möglich. Der Launcher startet mit normalen Rechten.\n" + ex.Message,
                    "Administratorrechte", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ClientLog.Write(LogSeverity.INFO, "Client", "Client wird beendet.");
        // Keep a bounded drain alive after WPF exits, without blocking its Dispatcher.
        var drain = ClientLog.Store.DisposeAsync().AsTask();
        if (!drain.IsCompleted)
            new Thread(() =>
            {
                try { drain.Wait(TimeSpan.FromSeconds(2)); }
                catch (Exception) { /* Logging must never prevent shutdown. */ }
            }) { IsBackground = false, Name = "Client log shutdown drain" }.Start();
        else if (drain.IsFaulted) _ = drain.Exception;
        base.OnExit(e);
    }
}
