using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace LanLauncher.Client;

/// <summary>Connects a Windows client to a NAS share using the current Windows session.</summary>
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

    internal static void EnsureConnected(string share)
    {
        if (Directory.Exists(share)) return;

        var resource = new NetResource { Type = ResourceTypeDisk, RemoteName = share };
        // Windows first tries its existing SMB session and saved Credential Manager credentials.
        var result = WNetAddConnection2(ref resource, null, null, 0);
        if (result == 0 || result == ErrorAlreadyAssigned || Directory.Exists(share)) return;

        if (result == ErrorSessionCredentialConflict)
            throw new IOException("Windows verwendet für diesen NAS-Server bereits andere SMB-Anmeldedaten. " +
                "Bitte die bestehende Verbindung in Windows prüfen (net use).");

        throw new IOException("Die NAS-Freigabe " + share + " konnte mit den Windows-Anmeldedaten " +
            "nicht verbunden werden: " + new Win32Exception(result).Message +
            " (Fehler " + result + "). Hinterlege bei Bedarf launcher_reader in der " +
            "Windows-Anmeldeinformationsverwaltung für den NAS-Server.");
    }
}
