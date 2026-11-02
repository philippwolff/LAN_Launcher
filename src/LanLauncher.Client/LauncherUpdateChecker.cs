using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace LanLauncher.Client;

internal static class LauncherUpdateChecker
{
    private const string RunsUrl =
        "https://api.github.com/repos/philippwolff/LAN_Launcher/actions/workflows/windows-client.yml/runs?branch=main&status=success&per_page=1";

    internal static async Task<(bool? Available, string Details)> CheckAsync()
    {
        var marker = Path.Combine(AppContext.BaseDirectory, "launcher-build.txt");
        if (!File.Exists(marker) || !long.TryParse(File.ReadAllText(marker).Trim(), out var installedRun))
            return (null, "Build-ID fehlt – Updateprüfung erst mit einem neuen GitHub-Actions-Paket möglich.");

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var request = new HttpRequestMessage(HttpMethod.Get, RunsUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("LANLauncher", "1.0"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            // For private repositories, users may optionally provide a fine-grained,
            // read-only Actions token via the environment. Never store a token in the app.
            var token = Environment.GetEnvironmentVariable("LANLAUNCHER_GITHUB_TOKEN");
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());

            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return (null, "GitHub-Abfrage nicht möglich (HTTP " + (int)response.StatusCode +
                    "). Bei privaten Repositories ist eine Berechtigung erforderlich.");

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var json = await JsonDocument.ParseAsync(stream);
            var runs = json.RootElement.GetProperty("workflow_runs");
            if (runs.GetArrayLength() == 0)
                return (null, "Noch kein erfolgreicher GitHub-Build gefunden.");

            var latest = runs[0].GetProperty("id").GetInt64();
            return latest > installedRun
                ? (true, "Ein neuer Windows-Client-Build ist verfügbar.")
                : (false, "Du verwendest den neuesten erfolgreichen Windows-Client-Build.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or
            IOException or JsonException or UnauthorizedAccessException)
        {
            return (null, "Updateprüfung derzeit nicht möglich: " + ex.Message);
        }
    }
}
