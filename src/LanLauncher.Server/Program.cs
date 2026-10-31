using System.Collections.Concurrent;
using LanLauncher.Shared;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["ListenUrl"] ?? "http://0.0.0.0:8080");
var app = builder.Build();
var players = new ConcurrentDictionary<string, (PlayerInfo Player, DateTimeOffset Seen)>();
var games = new List<GameInfo>();
var gate = new object();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", service = "LAN Launcher" }));
app.MapGet("/api/games", () => { lock (gate) return Results.Ok(games.ToArray()); });
app.MapGet("/api/players", () => Results.Ok(players.Values.Where(x => DateTimeOffset.UtcNow - x.Seen < TimeSpan.FromSeconds(45)).Select(x => x.Player).OrderBy(x => x.Name).ToArray()));
app.MapPost("/api/join", (JoinRequest req) =>
{
    var name = req.Name.Trim();
    if (name.Length is < 2 or > 32 || string.IsNullOrWhiteSpace(req.ClientId)) return Results.BadRequest("Ungültiger Name oder ClientId.");
    var existing = players.Values.FirstOrDefault(x => x.Player.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && x.Player.Name != "" && DateTimeOffset.UtcNow - x.Seen < TimeSpan.FromSeconds(45));
    if (existing.Player is not null && !players.ContainsKey(req.ClientId)) return Results.Conflict("Benutzername ist bereits online.");
    // All public joins are User. Admin privileges require a separate authenticated channel later.
    var player = new PlayerInfo(name, "User", "Online");
    players[req.ClientId] = (player, DateTimeOffset.UtcNow);
    return Results.Ok(new JoinResponse(name, "User", req.ClientId));
});
app.MapPost("/api/status", (StatusRequest req) =>
{
    if (!players.TryGetValue(req.ClientId, out var current)) return Results.NotFound();
    if (req.Status is not ("Online" or "Im Spiel")) return Results.BadRequest();
    players[req.ClientId] = (current.Player with { Status = req.Status, GameId = req.Status == "Im Spiel" ? req.GameId : null }, DateTimeOffset.UtcNow);
    return Results.Ok();
});
app.Run();
