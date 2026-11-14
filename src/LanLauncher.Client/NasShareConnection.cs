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

    internal static void EnsureConnected(string share, bool admin)
    {
        var account = admin ? "launcher_admin" : "launcher_reader";
        if (Directory.Exists(share))
        {
            if (!admin) return;
            // A readable share is not proof of admin write permission.
            if (CanWrite(share)) return;
            throw new IOException("Die NAS ist erreichbar, aber die bestehende SMB-Sitzung hat keinen " +
                "Schreibzugriff. Windows kann nicht gleichzeitig launcher_reader und launcher_admin " +
                "für denselben NAS-Server verwenden. Schließe NAS-Dateien und trenne gezielt " +
                "die bestehende Freigabe mit 'net use " + share + " /delete'. " +
                "Verbinde danach mit launcher_admin. Bestehende Sitzungen werden nicht automatisch getrennt.");
        }

        var resource = new NetResource { Type = ResourceTypeDisk, RemoteName = share };
        // Explicitly request the role-specific NAS identity. Windows uses cached credentials
        // when available; no password is stored by the launcher.
        var result = WNetAddConnection2(ref resource, null, account, 0);
        if (result != 0 && result != ErrorAlreadyAssigned)
        {
            if (result == ErrorSessionCredentialConflict)
                throw new IOException("Eine SMB-Sitzung mit anderen NAS-Zugangsdaten ist bereits aktiv. " +
                    "Bitte die betroffene Freigabe in Windows gezielt trennen.");
            throw new IOException("NAS-Anmeldung als " + account + " fehlgeschlagen: " +
                new Win32Exception(result).Message + " (Fehler " + result + "). " +
                "Bitte die Zugangsdaten für 192.168.68.250 in der Windows-Anmeldeinformationsverwaltung hinterlegen.");
        }

        if (!Directory.Exists(share))
            throw new IOException("Die NAS-Freigabe " + share + " ist nicht erreichbar.");

        if (admin && !CanWrite(share))
            throw new IOException("NAS-Verbindung besteht, aber launcher_admin hat keinen Schreibzugriff auf " + share + ".");
    }

    private static bool CanWrite(string share)
    {
        // Check the actual game upload directory, not only the share root.
        var directory = Path.Combine(share, "Games");
        if (!Directory.Exists(directory)) return false;
        var probe = Path.Combine(directory, ".lanlauncher-write-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        finally
        {
            try { if (File.Exists(probe)) File.Delete(probe); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
