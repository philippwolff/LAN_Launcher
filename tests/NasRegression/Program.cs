using System.ComponentModel;
using System.Net;
using LanLauncher.Client;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine("PASS: " + name);
    checks++;
}
var directory = Path.Combine(Path.GetTempPath(), "LanLauncher-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var testLog = new LanLauncher.Client.Logging.ClientLogStore(new(Path.Combine(directory, "Logs")));
LanLauncher.Client.Logging.ClientLog.Configure(testLog);
try
{
    Check(!NasShareConnection.HasRequiredAccess(directory, false), "Share root alone is insufficient");
    Directory.CreateDirectory(Path.Combine(directory, "Games"));
    Check(NasShareConnection.HasRequiredAccess(directory, false), "Readable game directory is reused");
    Check(NasShareConnection.HasRequiredAccess(directory, true), "Writable game directory is reused");
    Check(!Directory.EnumerateFiles(Path.Combine(directory, "Games")).Any(), "Write probe is removed");
    Check(NasShareConnection.IsCredentialError(new Win32Exception(1326)), "Bad password is retryable");
    Check(NasShareConnection.IsCredentialError(new UnauthorizedAccessException()), "Access denied is retryable");
    Check(!NasShareConnection.IsCredentialError(new Win32Exception(1219)), "SMB conflict must not retry passwords");
    var caps = new TransferCapabilities(1, "smb", @"\\nas\share");
    try { ServerTransferResolver.ResolveSmbPackagePath(caps, "../secret"); throw new Exception("Traversal accepted"); }
    catch (ArgumentException) { Check(true, "Traversal rejected"); }

    using var http = new HttpClient(new Handler(false));
    var target = Path.Combine(directory, "package.zip");
    var progress = 0L;
    var name = await GamePackageTransfer.DownloadAsync(http, "http://test", "game", target,
        (received, _) => progress = received, default, ensureNasConnected: _ => throw new Exception("HTTP requested NAS login"));
    Check(name == "game.zip" && File.ReadAllText(target) == "package" && progress == 7,
        "Legacy HTTP download preserves payload and progress without NAS login");
    using var smb = new HttpClient(new Handler(true));
    var prompts = 0;
    Task Login(string share)
    {
        Check(share == @"\\nas\share", "Login receives correct share");
        prompts++;
        throw new OperationCanceledException();
    }
    var packageName = await GamePackageTransfer.GetPackageNameAsync(smb, "http://test", "game", default);
    Check(packageName == "game.zip" && prompts == 0, "Package name needs metadata only, no SMB probe or login");
    try { await GamePackageTransfer.DownloadAsync(smb, "http://test", "game", Path.Combine(directory, "unused"), (_, _) => { }, default, ensureNasConnected: Login); }
    catch (OperationCanceledException) { }
    Check(prompts == 1 && !File.Exists(Path.Combine(directory, "unused")), "Download awaits login and honors cancellation before destination creation");
    await DownloadRegression.RunAsync(Check, directory);
    await PreparationRegression.RunAsync(Check, directory);
    await OpenRegression.RunAsync(Check, directory);
    await LoggingRegression.RunAsync(Check, directory);
    await OpeningWaitRegression.RunAsync(Check, directory);
    Console.WriteLine($"{checks} checks passed.");
}
finally { await testLog.DisposeAsync(); Directory.Delete(directory, true); }

sealed class Handler(bool smb) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath == "/api/capabilities")
            return Task.FromResult(smb
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { protocolVersion = 1, downloadMode = "smb", smbShare = @"\\nas\share" })) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        if (smb) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"relativePath\":\"Games/game.zip\"}") });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("package") });
    }
}
