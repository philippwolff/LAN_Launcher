using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LanLauncher.Shared;
using LanLauncher.Server;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["ListenUrl"] ?? "http://0.0.0.0:8080");
builder.Services.AddSingleton<GameCatalog>();
var app = builder.Build();
var players = new ConcurrentDictionary<string, (PlayerInfo Player, DateTimeOffset Seen)>();
var catalog = app.Services.GetRequiredService<GameCatalog>();
var adminKey = builder.Configuration["AdminKey"];
if (string.IsNullOrWhiteSpace(adminKey))
    Console.WriteLine("WARNUNG: Admin-Funktionen deaktiviert. AdminKey per Umgebungsvariable LANLAUNCHER_AdminKey setzen.");

bool IsAdmin(HttpRequest request)
{
    if (string.IsNullOrWhiteSpace(adminKey)) return false;
    var supplied = request.Headers["X-Admin-Key"].ToString();
    return CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(adminKey)),
        SHA256.HashData(Encoding.UTF8.GetBytes(supplied)));
}

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", service = "LAN Launcher" }));
app.MapGet("/api/games", () => Results.Ok(catalog.List()));
app.MapGet("/api/games/{id}/package", (string id) =>
{
    var package = catalog.Package(id);
    return package is null ? Results.NotFound() : Results.File(package.Value.Path, "application/octet-stream", package.Value.Name);
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
app.Run();

public record ImportGameRequest(string FileName, string Name, string Version, string? Description);
