using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanLauncher.Shared;
using LanLauncher.Server;

var builder = WebApplication.CreateBuilder(args);
var libraryLocationFile = Path.Combine(AppContext.BaseDirectory, "library-location.json");
if (File.Exists(libraryLocationFile))
{
    using var savedLocation = JsonDocument.Parse(File.ReadAllText(libraryLocationFile));
    var savedPath = savedLocation.RootElement.GetProperty("StoragePath").GetString();
    if (!string.IsNullOrWhiteSpace(savedPath))
        builder.Configuration["StoragePath"] = savedPath;
}

builder.WebHost.UseUrls(builder.Configuration["ListenUrl"] ?? "http://0.0.0.0:8080");
builder.Services.AddSingleton<GameCatalog>();
builder.Services.AddSingleton<GameTagCatalog>();
builder.Services.AddSingleton<GameStartNoticeCatalog>();
builder.Services.AddSingleton<ModCatalog>();
builder.Services.AddSingleton<ServerLibraryService>();
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 20L * 1024 * 1024 * 1024);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options => options.MultipartBodyLengthLimit = 20L * 1024 * 1024 * 1024);
var app = builder.Build();
var players = new ConcurrentDictionary<string, (PlayerInfo Player, DateTimeOffset Seen)>();
var catalog = app.Services.GetRequiredService<GameCatalog>();
var tagCatalog = app.Services.GetRequiredService<GameTagCatalog>();
var startNotices = app.Services.GetRequiredService<GameStartNoticeCatalog>();
var modCatalog = app.Services.GetRequiredService<ModCatalog>();
var libraryService = app.Services.GetRequiredService<ServerLibraryService>();
var adminKeys = new AdminKeyStore(builder.Configuration);
if (!adminKeys.Configured)
    Console.WriteLine("AdminKey noch nicht eingerichtet. Öffne http://localhost:8080/admin auf dem Server-PC.");

bool IsAdmin(HttpRequest request) => adminKeys.Verify(request.Headers["X-Admin-Key"].ToString());

bool IsLocal(HttpContext context) =>
    context.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);

app.MapGet("/api/admin/key/status", (HttpContext context) =>
    !IsLocal(context) ? Results.StatusCode(403) : Results.Ok(new { configured = adminKeys.Configured }));
app.MapPost("/api/admin/key/set", (HttpContext context, SetAdminKeyRequest data) =>
{
    if (!IsLocal(context)) return Results.StatusCode(403);
    try
    {
        return adminKeys.TrySet(data.OldKey, data.NewKey)
            ? Results.Ok(new { status = "saved" })
            : Results.Unauthorized();
    }
    catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
});

app.MapGet("/admin", (HttpContext context) =>
{
    if (!IsLocal(context)) return Results.StatusCode(403);
    var file = Path.Combine(AppContext.BaseDirectory, "wwwroot", "admin.html");
    return Results.File(file, "text/html; charset=utf-8");
});
app.MapGet("/api/admin/dashboard", (HttpContext context) =>
{
    if (!IsLocal(context)) return Results.StatusCode(403);
    var addresses = NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up &&
            n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
            n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork &&
            !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
        .Select(a => a.Address.ToString()).Distinct().ToArray();
    var marker = Path.Combine(AppContext.BaseDirectory, "launcher-version.txt");
    var version = File.Exists(marker) ? File.ReadAllText(marker).Trim() : "development";
    return Results.Ok(new { status = "Online", port = new Uri(builder.Configuration["ListenUrl"] ?? "http://0.0.0.0:8080").Port,
        addresses, version, players = players.Values
            .Where(x => DateTimeOffset.UtcNow - x.Seen < TimeSpan.FromSeconds(45))
            .Select(x => x.Player).OrderBy(x => x.Name).ToArray(),
        games = catalog.List() });
});
app.MapGet("/api/admin/library/location", (HttpContext context) =>
{
    if (!IsLocal(context)) return Results.StatusCode(403);
    if (!IsAdmin(context.Request)) return Results.Unauthorized();
    return Results.Ok(new { path = Path.GetFullPath(builder.Configuration["StoragePath"] ??
        Path.Combine(AppContext.BaseDirectory, "Storage")) });
});
app.MapPost("/api/admin/library/location", (HttpContext context, LibraryLocationRequest data) =>
{
    if (!IsLocal(context)) return Results.StatusCode(403);
    if (!IsAdmin(context.Request)) return Results.Unauthorized();
    try
    {
        if (string.IsNullOrWhiteSpace(data.Path) || !Path.IsPathFullyQualified(data.Path))
            return Results.BadRequest("Bitte einen absoluten Ordnerpfad angeben.");
        var destination = Path.GetFullPath(data.Path.Trim());
        Directory.CreateDirectory(destination);
        var existing = Path.GetFullPath(builder.Configuration["StoragePath"] ??
            Path.Combine(AppContext.BaseDirectory, "Storage"));
        if (string.Equals(destination.TrimEnd(Path.DirectorySeparatorChar),
            existing.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return Results.Ok(new { path = destination, restartRequired = false });
        if (Directory.EnumerateFileSystemEntries(destination).Any())
            return Results.BadRequest("Der Zielordner muss leer sein. Bestehende Bibliotheken bitte vorab sichern und separat übernehmen.");
        // Keep the original library untouched. Copy metadata and packages before switching.
        foreach (var file in Directory.GetFiles(existing))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var folder in Directory.GetDirectories(existing))
        {
            var name = Path.GetFileName(folder);
            if (name.StartsWith("Backup-", StringComparison.OrdinalIgnoreCase)) continue;
            CopyLibraryDirectory(folder, Path.Combine(destination, name));
        }
        var temp = libraryLocationFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new { StoragePath = destination }));
        File.Move(temp, libraryLocationFile, true);
        return Results.Ok(new { path = destination, restartRequired = true });
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
    {
        return Results.BadRequest("Speicherort konnte nicht geändert werden: " + ex.Message);
    }
});

app.MapPost("/api/admin/dashboard/open-folder", (HttpContext context) =>
{
    if (!IsLocal(context)) return Results.StatusCode(403);
    if (!IsAdmin(context.Request)) return Results.Unauthorized();
    var folder = Path.Combine(Path.GetFullPath(builder.Configuration["StoragePath"] ??
        Path.Combine(AppContext.BaseDirectory, "Storage")), "Packages");
    Directory.CreateDirectory(folder);
    Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = true });
    return Results.Ok();
});
app.MapPost("/api/admin/dashboard/stop", (HttpContext context, IHostApplicationLifetime lifetime) =>
{
    if (!IsLocal(context)) return Results.StatusCode(403);
    if (!IsAdmin(context.Request)) return Results.Unauthorized();
    lifetime.StopApplication();
    return Results.Ok(new { status = "stopping" });
});
app.MapGet("/api/admin/dashboard/genres", (HttpContext context) =>
    !IsLocal(context) ? Results.StatusCode(403) : Results.Ok(GameTagOptions.Genres));

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", service = "LAN Launcher" }));
app.MapGet("/api/games", () => Results.Ok(catalog.List()));
app.MapGet("/api/games/{id}/start-notice", (string id) =>
    catalog.List().Any(g => g.Id == id)
        ? Results.Ok(new GameStartNotice(startNotices.Get(id))) : Results.NotFound());
app.MapPut("/api/admin/games/{id}/start-notice", (HttpRequest req, string id, GameStartNotice notice) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();
    if (!catalog.List().Any(g => g.Id == id)) return Results.NotFound();
    try { startNotices.Set(id, notice.Text ?? ""); return Results.Ok(new GameStartNotice(startNotices.Get(id))); }
    catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
});
app.MapGet("/api/games/{id}/tags", (string id) =>
    catalog.List().Any(g => g.Id == id) ? Results.Ok(tagCatalog.Get(id)) : Results.NotFound());
app.MapPut("/api/admin/games/{id}/tags", (HttpRequest req, string id, GameTags tags) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();
    if (!catalog.List().Any(g => g.Id == id)) return Results.NotFound();
    try { tagCatalog.Set(id, tags); return Results.Ok(tagCatalog.Get(id)); }
    catch (ArgumentException e) { return Results.BadRequest(e.Message); }
});
app.MapGet("/api/games/{id}/mod", (string id) =>
    catalog.List().Any(g => g.Id == id) ? Results.Ok(modCatalog.Get(id)) : Results.NotFound());
app.MapPut("/api/admin/games/{id}/mod", (HttpRequest req, string id, ModConfiguration config) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();
    if (id == config.BaseGameId || !catalog.List().Any(g => g.Id == id) ||
        !catalog.List().Any(g => g.Id == config.BaseGameId)) return Results.BadRequest("Ungültiges Hauptspiel.");
    try { modCatalog.Set(id, config); return Results.Ok(); }
    catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
});
app.MapGet("/api/games/{id}/package", (string id) =>
{
    var package = catalog.Package(id);
    return package is null ? Results.NotFound() : Results.File(package.Value.Path, "application/octet-stream", package.Value.Name);
});
app.MapGet("/api/games/{id}/launch-path", (string id) =>
{
    var path = catalog.GetLaunchPath(id);
    return path is null ? Results.NotFound() : Results.Ok(new LaunchPathRequest(path));
});
app.MapPut("/api/admin/games/{id}/launch-path", (HttpRequest req, string id, LaunchPathRequest data) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();
    try { catalog.SetLaunchPath(id, data.Path); return Results.Ok(); }
    catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
});
app.MapGet("/api/players", () => Results.Ok(players.Values
    .Where(x => DateTimeOffset.UtcNow - x.Seen < TimeSpan.FromSeconds(45))
    .Select(x => x.Player).OrderBy(x => x.Name).ToArray()));
app.MapPost("/api/join", (JoinRequest req) =>
{
    var name = req.Name?.Trim() ?? "";
    if (name.Length is < 2 or > 32 || string.IsNullOrWhiteSpace(req.ClientId))
        return Results.BadRequest("Ungültiger Name oder ClientId.");
    if (players.Any(x => x.Key != req.ClientId &&
        x.Value.Player.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
        DateTimeOffset.UtcNow - x.Value.Seen < TimeSpan.FromSeconds(45)))
        return Results.Conflict("Benutzername ist bereits online.");
    players[req.ClientId] = (new PlayerInfo(name, "User", "Online"), DateTimeOffset.UtcNow);
    return Results.Ok(new JoinResponse(name, "User", req.ClientId));
});
app.MapPost("/api/status", (StatusRequest req) =>
{
    if (!players.TryGetValue(req.ClientId, out var current)) return Results.NotFound();
    if (req.Status is not ("Online" or "Im Spiel")) return Results.BadRequest();
    players[req.ClientId] = (current.Player with { Status = req.Status, GameId = req.Status == "Im Spiel" ? req.GameId : null }, DateTimeOffset.UtcNow);
    return Results.Ok();
});
app.MapGet("/api/admin/library/pending", (HttpRequest req) =>
    !IsAdmin(req) ? Results.Unauthorized() : Results.Ok(libraryService.DiscoverUnregisteredPackages()));
app.MapPost("/api/admin/library/register", (HttpRequest req, RegisterPackageRequest data) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();
    try
    {
        var game = libraryService.RegisterPackage(data.FileName, data.Name, data.Version, data.Description, data.Tags);
        catalog.ReloadFromDisk();
        return Results.Ok(game);
    }
    catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or IOException)
    { return Results.BadRequest(ex.Message); }
});
app.MapPost("/api/admin/library/sync", (HttpRequest req) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();
    var result = libraryService.Check(true);
    catalog.ReloadFromDisk();
    return Results.Ok(result);
});
app.MapGet("/api/admin/library/check", (HttpRequest req) =>
    !IsAdmin(req) ? Results.Unauthorized() : Results.Ok(libraryService.Check(false)));
app.MapPost("/api/admin/library/check", (HttpRequest req) =>
    !IsAdmin(req) ? Results.Unauthorized() : Results.Ok(libraryService.Check(true)));
app.MapGet("/api/admin/library/export", (HttpRequest req) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();
    var path = libraryService.Export();
    var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
        128 * 1024, FileOptions.SequentialScan | FileOptions.DeleteOnClose);
    return Results.File(stream, "application/zip", "LANLauncher-Serverbibliothek.zip",
        enableRangeProcessing: false);
});
app.MapPost("/api/admin/library/import", async (HttpRequest req) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();
    if (!req.HasFormContentType) return Results.BadRequest("ZIP-Datei erwartet.");
    var form = await req.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0) return Results.BadRequest("Archiv fehlt.");
    try
    {
        using var stream = file.OpenReadStream();
        return Results.Ok(libraryService.Import(stream));
    }
    catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException)
    { return Results.BadRequest(ex.Message); }
}).DisableAntiforgery();
app.MapGet("/api/admin/check", (HttpRequest req) => IsAdmin(req) ? Results.Ok(new { status = "authorized" }) : Results.Unauthorized());
app.MapGet("/api/admin/inbox", (HttpRequest req) =>
    !IsAdmin(req) ? Results.Unauthorized() : Results.Ok(
        Directory.GetFiles(catalog.Inbox).Select(Path.GetFileName).Where(n => n is not null &&
            (n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))).ToArray()));
app.MapPost("/api/admin/import", (HttpRequest req, ImportGameRequest data) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();
    try { return Results.Ok(catalog.Import(data.FileName, data.Name, data.Version, data.Description)); }
    catch (Exception e) when (e is ArgumentException or FileNotFoundException or IOException)
    { return Results.BadRequest(e.Message); }
});
app.MapPost("/api/admin/upload", async (HttpRequest req) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();
    if (!req.HasFormContentType) return Results.BadRequest("Multipart-Formular erwartet.");
    var form = await req.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0) return Results.BadRequest("Datei fehlt.");
    if (file.Length > 20L * 1024 * 1024 * 1024) return Results.BadRequest("Datei zu groß.");
    try
    {
        using var stream = file.OpenReadStream();
        return Results.Ok(catalog.Upload(stream, file.FileName, form["name"].ToString(), form["version"].ToString(), form["description"].ToString()));
    }
    catch (Exception e) when (e is ArgumentException or IOException) { return Results.BadRequest(e.Message); }
}).DisableAntiforgery();
app.MapDelete("/api/admin/games/{id}", (HttpRequest req, string id) =>
    !IsAdmin(req) ? Results.Unauthorized() : catalog.Remove(id) ? Results.Ok() : Results.NotFound());
static void CopyLibraryDirectory(string source, string target)
{
    Directory.CreateDirectory(target);
    foreach (var file in Directory.GetFiles(source))
        File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
    foreach (var folder in Directory.GetDirectories(source))
        CopyLibraryDirectory(folder, Path.Combine(target, Path.GetFileName(folder)));
}

app.Run();

public record ImportGameRequest(string FileName, string Name, string Version, string? Description);

public record LaunchPathRequest(string Path);

public record GameStartNotice(string Text);

public record RegisterPackageRequest(string FileName, string Name, string Version, string? Description, GameTags Tags);

public record SetAdminKeyRequest(string? OldKey, string NewKey);

public record LibraryLocationRequest(string Path);
