using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace LanLauncher.Client;

/// <summary>Connects to the NAS with a role-specific account. Credentials are managed by Windows.</summary>
internal static class NasShareConnection
{
    private const int ResourceTypeDisk = 1;
    private const int ErrorAlreadyAssigned = 85;
    private const int ErrorSessionCredentialConflict = 1219;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetAddConnection2W")]
    private static extern int WNetAddConnection2(ref NetResource resource,
        string? password, string? username, int flags);

    internal static bool HasRequiredAccess(string share, bool admin, NasPreparation? preparation = null)
    {
        try
        {
            // Existence alone does not prove permission to list game packages.
            var directory = Path.Combine(share, "Games");
            var entries = Step(preparation, "Verzeichnisauflistung: Enumerator öffnen für " + directory,
                () => Directory.EnumerateFileSystemEntries(directory).GetEnumerator());
            try
            {
                Step(preparation, "Verzeichnisauflistung: ersten Eintrag lesen (MoveNext) für " + directory,
                    () => entries.MoveNext());
            }
            finally { Step(preparation, "Verzeichnisauflistung: Enumerator schließen für " + directory, entries.Dispose); }
            if (admin) return CanWrite(share, preparation);
            preparation?.Report("Leseprüfung erfolgreich; bestehender Windows-Zugriff wird wiederverwendet. Keine Credentials und kein WNetAddConnection2.");
            return true;
        }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException ex) when (NasNetworkOperations.IsAuthenticationError(ex) || NasNetworkOperations.IsPermissionError(ex)) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    internal static void EnsureConnected(string share, bool admin, string? password = null, bool accessAlreadyChecked = false,
        NasPreparation? preparation = null)
    {
        var account = admin ? "launcher_admin" : "launcher_reader";
        if (!accessAlreadyChecked && HasRequiredAccess(share, admin, preparation)) return;
        if (admin && Step(preparation, "Vorhandene Freigabe vor Admin-Anmeldung prüfen (Directory.Exists)", () => Directory.Exists(share)))
        {
            // A readable share is not proof of admin write permission.
            throw new IOException("Die NAS ist erreichbar, aber die bestehende SMB-Sitzung hat keinen " +
                "Schreibzugriff. Windows kann nicht gleichzeitig launcher_reader und launcher_admin " +
                "für denselben NAS-Server verwenden. Schließe NAS-Dateien und trenne gezielt " +
                "die bestehende Freigabe mit 'net use " + share + " /delete'. " +
                "Verbinde danach mit launcher_admin. Bestehende Sitzungen werden nicht automatisch getrennt.");
        }

        var resource = new NetResource { Type = ResourceTypeDisk, RemoteName = share };
        // Explicitly request the role-specific NAS identity. Windows uses cached credentials
        // when available; no password is stored by the launcher.
        var result = Step(preparation, "SMB-Verbindungsaufbau: WNetAddConnection2 als " + account,
            () => WNetAddConnection2(ref resource, password, account, 0));
        preparation?.Report("WNetAddConnection2 beendet mit Windows-Code " + result);
        if (result != 0 && result != ErrorAlreadyAssigned)
        {
            if (result == ErrorSessionCredentialConflict)
                throw new IOException("Eine SMB-Sitzung mit anderen NAS-Zugangsdaten ist bereits aktiv. " +
                    "Bitte die betroffene Freigabe in Windows gezielt trennen.");
            throw new Win32Exception(result, "NAS-Anmeldung als " + account + " fehlgeschlagen: " +
                new Win32Exception(result).Message + " (Fehler " + result + ").");
        }

        if (!HasRequiredAccess(share, admin, preparation))
            throw new UnauthorizedAccessException("NAS-Verbindung besteht, aber die erforderlichen Berechtigungen fehlen auf " + share + ".");
    }

    internal static bool IsCredentialError(Exception ex) => NasNetworkOperations.IsAuthenticationError(ex) ||
        NasNetworkOperations.IsPermissionError(ex);

    private static bool CanWrite(string share, NasPreparation? preparation)
    {
        // Check the actual game upload directory, not only the share root.
        var directory = Path.Combine(share, "Games");
        var probe = Path.Combine(directory, ".lanlauncher-write-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var stream = Step(preparation, "Admin-Schreibprüfung: Testdatei anlegen in " + directory,
                () => new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None));
            Step(preparation, "Admin-Schreibprüfung: Testdatei schließen", stream.Dispose);
            return true;
        }
        catch (IOException ex) when (NasNetworkOperations.IsAuthenticationError(ex) || NasNetworkOperations.IsPermissionError(ex)) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        finally
        {
            try { Step(preparation, "Admin-Schreibprüfung: Testdatei löschen", () => File.Delete(probe)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static T Step<T>(NasPreparation? preparation, string operation, Func<T> action) =>
        preparation is null ? action() : preparation.Step(operation, action);

    private static void Step(NasPreparation? preparation, string operation, Action action)
    {
        if (preparation is null) action();
        else preparation.Step(operation, action);
    }
}
