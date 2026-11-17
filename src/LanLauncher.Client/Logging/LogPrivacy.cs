using System.Text.RegularExpressions;

namespace LanLauncher.Client.Logging;

internal static class LogPrivacy
{
    private static readonly AsyncLocal<string[]?> Secrets = new();
    private static readonly Regex Credentials = new(
        "(?i)([\\\"']?(?:password|passwd|pwd|passwort|access[_-]?token|refresh[_-]?token|token|authorization|x-admin-key|admin[_-]?key)[\\\"']?\\s*[:=]\\s*)(?:\\\"[^\\\"]*\\\"|'[^']*'|[^\\s;,}&]+)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Bearer = new(@"(?i)\b(Bearer|Basic)\s+[^\s,;""']+", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    internal static IDisposable Protect(string? secret)
    {
        var previous = Secrets.Value;
        Secrets.Value = string.IsNullOrEmpty(secret) ? previous : [.. previous ?? [], secret];
        return new Restore(() => Secrets.Value = previous);
    }

    internal static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        try
        {
            foreach (var secret in Secrets.Value ?? [])
            {
                value = value.Replace(secret, "[GEHEIM]", StringComparison.Ordinal);
                value = value.Replace(Uri.EscapeDataString(secret), "[GEHEIM]", StringComparison.OrdinalIgnoreCase);
            }
            value = Bearer.Replace(value, "$1 [GEHEIM]");
            value = Credentials.Replace(value, "$1[GEHEIM]");
            // URL user-info and query strings are never needed to diagnose a connection.
            value = Regex.Replace(value, @"(?i)(https?://)[^\s/@]+:[^\s/@]*@", "$1[GEHEIM]@", RegexOptions.None, TimeSpan.FromMilliseconds(100));
            value = Regex.Replace(value, @"(?i)(https?://[^\s?]+)\?[^\s]+", "$1?[QUERY ENTFERNT]", RegexOptions.None, TimeSpan.FromMilliseconds(100));
            return value;
        }
        catch (RegexMatchTimeoutException) { return "[Meldung aus Sicherheitsgründen entfernt]"; }
    }

    internal static string Anonymize(string text)
    {
        text = Redact(text);
        try
        {
            text = Regex.Replace(text, @"(?i)(?:[a-z]:\\|\\\\)[^\r\n\t;""<>]*", "[PFAD]", RegexOptions.None, TimeSpan.FromMilliseconds(100));
            text = Regex.Replace(text, @"\bS-1-\d+(?:-\d+)+\b", "[SID]", RegexOptions.None, TimeSpan.FromMilliseconds(100));
            text = Regex.Replace(text, @"(?i)(\b(?:username|user|benutzer|client-name)\s*[:=]\s*)(?:""[^""]*""|[^;\r\n\t]+)", "$1[BENUTZER]", RegexOptions.None, TimeSpan.FromMilliseconds(100));
            if (!string.IsNullOrWhiteSpace(Environment.UserName))
                text = Regex.Replace(text, @"(?i)\b" + Regex.Escape(Environment.UserName) + @"\b", "[BENUTZER]", RegexOptions.None, TimeSpan.FromMilliseconds(100));
            return text;
        }
        catch (RegexMatchTimeoutException) { return "[Meldung für anonymisierten Export entfernt]"; }
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        private bool disposed;
        public void Dispose() { if (!disposed) { disposed = true; restore(); } }
    }
}
