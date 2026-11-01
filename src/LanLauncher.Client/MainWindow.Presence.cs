using System.Diagnostics;
using System.Net.Http.Json;
using LanLauncher.Shared;

namespace LanLauncher.Client;

public partial class MainWindow
{
    private Process? runningGameProcess;
    private string? runningGameId;

    private async Task UpdatePresenceAsync()
    {
        if (server is null) return;
        if (runningGameProcess is not null)
        {
            try
            {
                if (runningGameProcess.HasExited)
                {
                    runningGameProcess.Dispose();
                    runningGameProcess = null;
                    runningGameId = null;
                }
            }
            catch (InvalidOperationException)
            {
                runningGameProcess = null;
                runningGameId = null;
            }
        }
        var playing = runningGameProcess is not null && runningGameId is not null;
        using var response = await http.PostAsJsonAsync(server + "/api/status",
            new StatusRequest(clientId, playing ? "Im Spiel" : "Online",
                playing ? runningGameId : null));
        response.EnsureSuccessStatusCode();
    }

    private void TrackGameProcess(Process? process, string gameId)
    {
        if (process is null) return;
        runningGameProcess?.Dispose();
        runningGameProcess = process;
        runningGameId = gameId;
        _ = UpdatePresenceAsyncSafely();
    }

    private async Task UpdatePresenceAsyncSafely()
    {
        try { await UpdatePresenceAsync(); }
        catch (Exception) { /* Next periodic refresh retries presence. */ }
    }
}
