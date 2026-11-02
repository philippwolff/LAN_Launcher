using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace LanLauncher.Client;

internal sealed record LauncherRelease(Version Version, string Tag, string DownloadUrl, string PageUrl, string Notes);

internal static class LauncherUpdateChecker
{
    private const string LatestUrl = "https://api.github.com/repos/philippwolff/LAN_Launcher/releases/latest";
    private const string AssetName = "LAN_Party_Client-win-x64.zip";

    internal static async Task<(bool? Available, string Details, LauncherRelease? Release)> CheckAsync()
    {
        var marker = Path.Combine(AppContext.BaseDirectory, "launcher-version.txt");
        if (!File.Exists(marker) || !TryVersion(File.ReadAllText(marker).Trim(), out var installed))
            return (null, "Keine veröffentlichte Versionsnummer vorhanden. Bitte zuerst eine Release-Version installieren.", null);
        try
        {
            using var client = CreateClient(TimeSpan.FromSeconds(12));
            using var response = await client.GetAsync(LatestUrl);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return (null, "Noch kein öffentliches GitHub Release vorhanden.", null);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var json = await JsonDocument.ParseAsync(stream);
            var root = json.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!TryVersion(tag, out var latest))
                return (null, "Das neueste Release hat keine unterstützte Versionsnummer.", null);
            var assets = root.GetProperty("assets");
            string? download = null;
            foreach (var asset in assets.EnumerateArray())
                if (asset.GetProperty("name").GetString() == AssetName)
                    download = asset.GetProperty("browser_download_url").GetString();
            if (download is null)
                return (null, "Das neueste Release enthält noch kein Windows-Client-Paket.", null);
            var release = new LauncherRelease(latest, tag, download,
                root.GetProperty("html_url").GetString() ?? "",
                root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "");
            return latest > installed
                ? (true, "Update " + tag + " verfügbar (installiert: v" + installed + ").", release)
                : (false, "Aktuell: v" + installed + ". Kein neueres Release vorhanden.", null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
            or JsonException or UnauthorizedAccessException)
        {
            return (null, "Updateprüfung nicht möglich: " + ex.Message, null);
        }
    }

    internal static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LANLauncher", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static bool TryVersion(string tag, out Version version) =>
        Version.TryParse(tag.TrimStart('v', 'V'), out version!);
}
