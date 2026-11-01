using System.Diagnostics;
using System.IO;
using System.Text;

namespace LanLauncher.Client;

internal static class InstallerDetector
{
    public static string Detect(string path, string gameName)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            var metadata = string.Join(" ", version.FileDescription, version.ProductName,
                version.CompanyName, version.Comments);
            var fromMetadata = Classify(metadata);
            if (fromMetadata != "manual") return fromMetadata;

            using var stream = File.OpenRead(path);
            // Check PE header/installer stub and overlay footer; do not load huge installers.
            var start = ReadPart(stream, 0, (int)Math.Min(stream.Length, 16L * 1024 * 1024));
            var result = Classify(start);
            if (result != "manual") return result;
            if (stream.Length > 16L * 1024 * 1024)
            {
                var tail = ReadPart(stream, Math.Max(0, stream.Length - 8L * 1024 * 1024),
                    (int)Math.Min(stream.Length, 8L * 1024 * 1024));
                result = Classify(tail);
                if (result != "manual") return result;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }

        // 0 A.D.'s Windows setup is NSIS; useful if the package stub has no marker.
        if (gameName.Equals("0ad", StringComparison.OrdinalIgnoreCase) ||
            gameName.Equals("0 A.D.", StringComparison.OrdinalIgnoreCase))
            return "nsis";
        return "manual";
    }

    private static byte[] ReadPart(FileStream stream, long offset, int count)
    {
        stream.Position = offset;
        var bytes = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(bytes, read, count - read);
            if (n == 0) break;
            read += n;
        }
        if (read != count) Array.Resize(ref bytes, read);
        return bytes;
    }

    private static string Classify(byte[] data)
    {
        // ASCII and UTF-16 strings are both common in Windows installer stubs.
        var ascii = Encoding.Latin1.GetString(data);
        var result = Classify(ascii);
        if (result != "manual") return result;
        return Classify(Encoding.Unicode.GetString(data));
    }

    private static string Classify(string text)
    {
        if (text.Contains("Inno Setup", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("InnoSetup", StringComparison.OrdinalIgnoreCase))
            return "inno";
        if (text.Contains("NullsoftInst", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Nullsoft Install System", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("NSIS Error", StringComparison.OrdinalIgnoreCase))
            return "nsis";
        return "manual";
    }
}
