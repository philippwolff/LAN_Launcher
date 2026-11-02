using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace LanLauncher.Client;

public partial class App : Application
{
    private void Application_Startup(object sender, StartupEventArgs e)
    {
        var settings = LauncherSettings.Load();
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
}
