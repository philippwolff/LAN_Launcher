using System.IO;
using System.Security;
using System.Text;

namespace LanLauncher.Client;

/// <summary>
/// Provides safe, actionable guidance for SMB access errors. Never changes existing
/// Windows SMB sessions or stores NAS credentials on behalf of the user.
/// </summary>
internal static class SmbAccessHelp
{
    internal static string Explain(Exception error, string? share, bool write)
    {
        if (error is OperationCanceledException) return "Übertragung abgebrochen.";
        if (error is not IOException and not UnauthorizedAccessException and not SecurityException)
            return error.Message;

        var root = ShareRoot(share);
        var account = write ? "launcher_admin" : "launcher_reader";
        var action = write ? "Schreibzugriff" : "Lesezugriff";
        var message = new StringBuilder();
        message.Append("NAS-").Append(action).Append(" fehlgeschlagen: ").Append(error.Message);
        if (root is null) return message.ToString();
        message.Append("\n\nPrüfe die SMB-Verbindung in Windows PowerShell:");
        message.Append("\nnet use");
        message.Append("\n\nBei einem Berechtigungsproblem: Launcher und Explorer-Fenster mit NAS-Dateien schließen,");
        message.Append(" anschließend nur die betroffene Freigabe trennen:");
        message.Append("\nnet use ").Append(root).Append(" /delete");
        message.Append("\n\nDanach mit dem passenden NAS-Konto verbinden:");
        message.Append("\nnet use ").Append(root).Append(" /user:").Append(account).Append(" *");
        message.Append("\n\nWindows erlaubt normalerweise keine parallelen NAS-Anmeldungen");
        message.Append(" mit verschiedenen Konten am selben Server. Andere bestehende Verbindungen");
        message.Append(" werden vom Launcher nicht automatisch getrennt.");
        return message.ToString();
    }

    private static string? ShareRoot(string? share)
    {
        if (string.IsNullOrWhiteSpace(share) || !share.StartsWith(@"\\", StringComparison.Ordinal))
            return null;
        var parts = share[2..].Split('\\');
        if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0)
            return null;
        return @"\\" + parts[0] + @"\" + parts[1];
    }
}
