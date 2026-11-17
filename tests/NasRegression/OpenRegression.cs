using LanLauncher.Client;

static class OpenRegression
{
    internal static async Task RunAsync(Action<bool, string> check, string directory)
    {
        var callerContext = NasWindowsContext.Capture();
        var workerContext = await Task.Run(NasWindowsContext.Capture);
        static string Field(string description, string key) => description.Split(';').Select(s => s.Trim()).Single(s => s.StartsWith(key + "="));
        check(callerContext.Contains("SID=S-") && !callerContext.Contains("LogonId=unbekannt") && callerContext.Contains("Elevated="),
            "Windows diagnostics expose real SID, token authentication LUID and elevation without account lookup");
        check(Field(callerContext, "SID") == Field(workerContext, "SID") && Field(callerContext, "LogonId") == Field(workerContext, "LogonId"),
            "Ordinary Task.Run retains tested caller Windows identity/logon context");

        var path = Path.Combine(directory, "open-only.bin");
        var original = new byte[] { 1, 2, 3, 4, 5 };
        await File.WriteAllBytesAsync(path, original);
        using (var launcher = NasPackageFile.OpenRead(path))
        {
            check(launcher.IsAsync && launcher.CanRead && !launcher.CanWrite && launcher.Position == 0,
                "Shared launcher/probe factory preserves asynchronous read-only open without reading bytes");
            using var parallelReader = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            check(!parallelReader.IsAsync && parallelReader.CanRead, "File.Open comparison is synchronous and shares read access");
        }
        check((await File.ReadAllBytesAsync(path)).SequenceEqual(original), "Open/close comparison leaves file content unchanged");
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try { using var forbidden = NasPackageFile.OpenRead(path); throw new Exception("Exclusive lock ignored"); }
            catch (IOException error)
            {
                check(NasNetworkOperations.ErrorCode(error) == 32 && !NasNetworkOperations.IsTransient(error),
                    "Exclusive file lock yields sharing violation 32 and is not blindly retried");
            }
        }
        var phases = new List<string>();
        using var input = new ObservedReadStream(() => phases.Last().Contains("Lesezugriff beginnt"));
        await GamePackageTransfer.CopyAsync(input, Path.Combine(directory, "first-read.bin"), original.Length,
            (_, _) => { }, default, @"\\fake\share\Games\game.exe", phases.Add);
        check(input.SawStartBeforeRead && phases.Count(s => s.Contains("erster Lesezugriff beginnt")) == 1 &&
            phases.Count(s => s.Contains("erster Lesezugriff beendet")) == 1,
            "First-read diagnostics bracket actual ReadAsync exactly once, separate from file opening");
        check(phases.First().StartsWith("Downloadziel:"), "Destination opening is distinguished from first source read");
        phases.Clear();
        using var failedFirstRead = new BrokenFirstRead();
        try
        {
            await GamePackageTransfer.CopyAsync(failedFirstRead, Path.Combine(directory, "broken-first-read"), 5,
                (_, _) => { }, default, @"\\fake\share\Games\game.exe", phases.Add);
            throw new Exception("Read failure ignored");
        }
        catch (IOException)
        {
            check(phases.Any(s => s.Contains("erster Lesezugriff fehlgeschlagen") && s.Contains("59")) &&
                !phases.Any(s => s.Contains("Lesezugriff beendet")), "First-read error is diagnosed with its native code, never as a successful open/read");
        }
    }
}

sealed class ObservedReadStream(Func<bool> startReported) : MemoryStream(new byte[] { 1, 2, 3, 4, 5 })
{
    internal bool SawStartBeforeRead;
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        if (Position == 0) SawStartBeforeRead = startReported();
        return base.ReadAsync(buffer, token);
    }
}

sealed class BrokenFirstRead : MemoryStream
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
        throw new IOException("simulated first read failure", unchecked((int)0x8007003b));
}
