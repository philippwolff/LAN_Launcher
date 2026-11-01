namespace LanLauncher.Shared;

public record GameTags(int[] PlayerCounts, string[] Genres);

public static class GameTagOptions
{
    public static readonly string[] Genres =
    [
        "Action", "Abenteuer", "Arcade", "Aufbau", "Battle Royale", "Beat 'em up",
        "Brettspiel", "Casual", "Dungeon Crawler", "Echtzeitstrategie", "Flugsimulation",
        "Geschicklichkeit", "Horror", "Jump 'n' Run", "Kartenspiel", "Koop",
        "MOBA", "MMORPG", "Musik / Rhythmus", "Open World", "Partyspiel",
        "Puzzle / Rätsel", "Racing / Rennspiel", "Rollenspiel (RPG)",
        "Roguelike / Roguelite", "Sandbox", "Shooter", "Simulation",
        "Sport", "Stealth", "Strategie", "Survival", "Taktik",
        "Tower Defense", "Visual Novel", "Wirtschaftssimulation",
        "Rundenstrategie", "4X-Strategie", "Sonstiges"
    ];

    public static GameTags Validate(GameTags tags)
    {
        var counts = (tags.PlayerCounts ?? []).Distinct().OrderBy(n => n).ToArray();
        var genres = (tags.Genres ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (counts.Any(n => n < 1 || n > 16) ||
            genres.Any(g => !Genres.Contains(g, StringComparer.OrdinalIgnoreCase)))
            throw new ArgumentException("Ungültige Spieleranzahl oder Genre.");
        return new GameTags(counts, genres);
    }
}
