namespace LanLauncher.Shared;
public record GameInfo(string Id, string Name, string Version, string? Description = null);
public record PlayerInfo(string Name, string Role, string Status, string? GameId = null);
public record JoinRequest(string Name, string ClientId);
public record JoinResponse(string Name, string Role, string ClientId);
public record StatusRequest(string ClientId, string Status, string? GameId);
