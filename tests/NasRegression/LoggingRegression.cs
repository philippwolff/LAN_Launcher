using System.ComponentModel;
using System.Text.Json;
using LanLauncher.Client.Logging;

static class LoggingRegression
{
    internal static async Task RunAsync(Action<bool, string> check, string directory)
    {
        var mainStore = ClientLog.Store;
        await mainStore.FlushAsync();
        var previous = await mainStore.ReadAsync();
        check(previous.Entries.Any(e => e.Severity == LogSeverity.WARN && e.Message.Contains("Worker")),
            "Blocked/cancelled SMB workers are persisted as warnings without launching diagnostic SMB operations");
        check(previous.Entries.Any(e => e.Component == "SMB" && e.Message.Contains("LogonId=")),
            "SMB start/worker security contexts reach persistent client logging");

        var protectedDirectory = Path.Combine(directory, "protected-logs");
        await using (var store = new ClientLogStore(new(protectedDirectory)))
        {
            ClientLog.Configure(store);
            try
            {
                using (LogPrivacy.Protect("opaque-secret-012345"))
                using (ClientLog.BeginOperation("Privacy"))
                    ClientLog.Write(LogSeverity.ERROR, "Test", "opaque-secret-012345 password=nas-secret X-Admin-Key: admin-secret Authorization: Bearer bearer-secret",
                        new IOException("https://user:url-secret@host/path?token=query-secret", new Win32Exception(1326, "password=inner-secret")));
                var ids = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => Task.Run(async () =>
                {
                    using var operation = ClientLog.BeginOperation("Parallel" + i);
                    var id = ClientLog.OperationId;
                    await Task.Yield();
                    ClientLog.Write(LogSeverity.INFO, "Test", "Worker " + i);
                    return id;
                })));
                await store.FlushAsync();
                var snapshot = await store.ReadAsync();
                check(ids[0] != ids[1] && snapshot.Entries.Count(e => ids.Contains(e.OperationId) && e.Message.StartsWith("Worker")) == 2 && ClientLog.OperationId is null,
                    "Async download scopes preserve isolated correlation IDs across workers and restore their parent");
                var persisted = string.Join("", Directory.EnumerateFiles(protectedDirectory).Select(path =>
                {
                    using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                    return reader.ReadToEnd();
                }));
                check(new[] { "opaque-secret-012345", "nas-secret", "admin-secret", "bearer-secret", "url-secret", "query-secret", "inner-secret" }.All(secret => !persisted.Contains(secret)),
                    "Passwords, opaque protected secrets, admin keys, bearer credentials and URL credentials never enter disk logs");
                check(snapshot.Entries.Any(e => e.ExceptionDetails?.Contains("Windows=1326") == true && e.ExceptionDetails.Contains("Win32Exception")),
                    "Relevant inner exceptions and native Windows codes remain available after redaction");
                var input = new LogEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, LogSeverity.INFO, "SMB",
                    @"user=Alice; Pfad=C:\Users\Alice\Private Folder\game.exe; UNC=\\nas\Daten\Games\game.zip; SID=S-1-5-21-123-456-789-1001", "Download-1", null);
                var exported = LogFormat.Export([input], true);
                check(!exported.Contains("Alice") && !exported.Contains("Private Folder") && !exported.Contains(@"\\nas") && !exported.Contains("S-1-5-21-123"),
                    "Optional export anonymization removes users, local/UNC file paths and SID");
                check(LogFormat.Export([input], false).Contains("Alice"), "Non-anonymized export retains diagnostic paths when explicitly selected");
            }
            finally { ClientLog.Configure(mainStore); }
        }
        foreach (var input in new[]
        {
            "{\"password\":\"secret with spaces\"}", "Authorization: Basic secret", "token=secret&next=1",
            "Admin_Key='secret with spaces'", "Passwort: secret", "access_token=secret refresh_token=secret"
        })
            check(!LogPrivacy.Redact(input).Contains("secret"), "Credential redaction: " + input.Split(':', '=')[0]);

        var rotationDirectory = Path.Combine(directory, "rotation-logs");
        var options = new LogStoreOptions(rotationDirectory, MaxTotalBytes: 16 * 1024, MaxFileBytes: 4096, MemoryEntries: 8);
        await using (var store = new ClientLogStore(options))
        {
            for (var i = 0; i < 90; i++) store.Write(LogSeverity.INFO, "Rotation", i + new string('x', 500));
            store.Write(LogSeverity.INFO, "Rotation", "restart-marker");
            await store.FlushAsync();
            var files = new DirectoryInfo(rotationDirectory).GetFiles("client-*.jsonl");
            check(files.Length > 1 && files.All(f => f.Length <= options.MaxFileBytes) && files.Sum(f => f.Length) <= options.MaxTotalBytes,
                "Rotation keeps per-file and total disk usage within the configured limits");
            check((await store.ReadAsync()).Entries.Count == 8, "Live log memory remains bounded");
        }
        await using (var restarted = new ClientLogStore(options))
        {
            await restarted.FlushAsync();
            check((await restarted.ReadAsync()).Entries.Any(e => e.Message == "restart-marker"), "Client logs survive restart and are reloaded into the viewer");
        }

        var retentionDirectory = Path.Combine(directory, "retention-logs");
        Directory.CreateDirectory(retentionDirectory);
        var old = Path.Combine(retentionDirectory, "client-old.jsonl");
        var retained = Path.Combine(retentionDirectory, "client-retained.jsonl");
        var history = JsonSerializer.Serialize(new LogEntry(Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-6), LogSeverity.INFO, "History", "retained-marker", null, null));
        await File.WriteAllTextAsync(old, history + "\n");
        await File.WriteAllTextAsync(retained, history + "\n{interrupted-final-line");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-9));
        File.SetLastWriteTimeUtc(retained, DateTime.UtcNow.AddDays(-6));
        await using (var store = new ClientLogStore(new(retentionDirectory)))
        {
            await store.FlushAsync();
            check(!File.Exists(old) && File.Exists(retained), "History inside seven days is retained when the size budget permits; expired files are removed");
            check((await store.ReadAsync()).Entries.Any(e => e.Message == "retained-marker"), "Interrupted final JSON line does not prevent history recovery");
        }
        var blocked = Path.Combine(directory, "not-a-directory");
        await File.WriteAllTextAsync(blocked, "block log directory creation");
        await using (var store = new ClientLogStore(new(blocked)))
        {
            store.Write(LogSeverity.ERROR, "Failure", "still-visible");
            await store.FlushAsync();
            var snapshot = await store.ReadAsync();
            check(snapshot.Entries.Any(e => e.Message == "still-visible") && snapshot.StorageStatus.Contains("nicht verfügbar"),
                "Disk failure cannot break the client; live entries and storage-error status remain visible");
        }
        await using (var store = new ClientLogStore(new(Path.Combine(directory, "saturated-logs"), QueueCapacity: 1)))
        {
            for (var i = 0; i < 2000; i++) store.Write(LogSeverity.DEBUG, "Flood", "entry " + i);
            await store.FlushAsync();
            check((await store.ReadAsync()).Entries.Any(e => e.Severity == LogSeverity.WARN && e.Message.Contains("Warteschlange voll")),
                "Saturated logging reports dropped entries instead of blocking the producer");
        }
    }
}
