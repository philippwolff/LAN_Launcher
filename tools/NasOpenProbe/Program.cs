using System.Diagnostics;
using System.Runtime.InteropServices;
using LanLauncher.Client;

if (args.Length is not (1 or 3) || args.Length == 3 && args[1] != "--profile")
{
    Console.Error.WriteLine("Usage: NasOpenProbe.exe <path> [--profile launcher|file-open|sync-128k|async-4k]");
    return 2;
}
var path = args[0];
var profile = args.Length == 3 ? args[2] : "launcher";
if (profile is not ("launcher" or "file-open" or "sync-128k" or "async-4k"))
{
    Console.Error.WriteLine("Unknown profile: " + profile);
    return 2;
}
Console.WriteLine($"Runtime={RuntimeInformation.FrameworkDescription}; OS={RuntimeInformation.OSDescription}; Architecture={RuntimeInformation.ProcessArchitecture}");
Console.WriteLine("Process=" + Environment.ProcessPath);
Console.WriteLine("Caller context: " + NasWindowsContext.Capture());
Console.WriteLine("Path=" + path);
return await Task.Run(() =>
{
    Console.WriteLine("Worker context: " + NasWindowsContext.Capture());
    Console.WriteLine("Parameters: " + (profile switch
    {
        "launcher" => NasPackageFile.Parameters,
        "sync-128k" => "Mode=Open, Access=Read, Share=Read, Buffer=131072, Options=None",
        "async-4k" => "Mode=Open, Access=Read, Share=Read, Buffer=4096, Options=Asynchronous",
        _ => "File.Open(path, Open, Read, Read); default buffer/options"
    }));
    FileStream? stream = null;
    var exitCode = 0;
    var clock = Stopwatch.StartNew();
    try
    {
        Console.WriteLine($"{DateTimeOffset.UtcNow:O} OPEN_START profile={profile}; no Read/Length/Copy/Execute");
        Console.Out.Flush();
        stream = profile switch
        {
            "launcher" => NasPackageFile.OpenRead(path),
            "sync-128k" => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, NasPackageFile.BufferSize, FileOptions.None),
            "async-4k" => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous),
            _ => File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)
        };
        Console.WriteLine($"{DateTimeOffset.UtcNow:O} OPEN_OK elapsedMs={clock.Elapsed.TotalMilliseconds:0.0}; IsAsync={stream.IsAsync}");
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"OPEN_ERROR elapsedMs={clock.Elapsed.TotalMilliseconds:0.0}; {error.GetType().Name}; HRESULT=0x{error.HResult:X8}; {error.Message}");
        exitCode = 1;
    }
    finally
    {
        if (stream is not null)
        {
            clock.Restart();
            Console.WriteLine($"{DateTimeOffset.UtcNow:O} CLOSE_START");
            Console.Out.Flush();
            try
            {
                stream.Dispose();
                Console.WriteLine($"{DateTimeOffset.UtcNow:O} CLOSE_OK elapsedMs={clock.Elapsed.TotalMilliseconds:0.0}");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"CLOSE_ERROR elapsedMs={clock.Elapsed.TotalMilliseconds:0.0}; HRESULT=0x{error.HResult:X8}; {error.Message}");
                exitCode = 1;
            }
        }
    }
    return exitCode;
});
