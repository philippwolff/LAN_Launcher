using System.Security.Cryptography;
using System.Text.Json;

namespace LanLauncher.Server;

public sealed class AdminKeyStore
{
    private readonly object gate = new();
    private readonly string path;
    private string? legacyKey;
    private byte[]? salt;
    private byte[]? hash;

    public AdminKeyStore(IConfiguration config)
    {
        var root = Path.GetFullPath(config["StoragePath"] ?? Path.Combine(AppContext.BaseDirectory, "Storage"));
        Directory.CreateDirectory(root);
        path = Path.Combine(root, "admin-key.json");
        legacyKey = Environment.GetEnvironmentVariable("LANLAUNCHER_AdminKey") ?? config["AdminKey"];
        if (File.Exists(path))
        {
            var record = JsonSerializer.Deserialize<KeyRecord>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Ungültige AdminKey-Datei.");
            salt = Convert.FromBase64String(record.Salt);
            hash = Convert.FromBase64String(record.Hash);
            legacyKey = null;
        }
    }

    public bool Configured
    {
        get { lock (gate) return hash is not null || !string.IsNullOrWhiteSpace(legacyKey); }
    }

    public bool Verify(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return false;
        lock (gate)
        {
            if (hash is not null && salt is not null)
            {
                var computed = Rfc2898DeriveBytes.Pbkdf2(candidate, salt, 210_000, HashAlgorithmName.SHA256, 32);
                return CryptographicOperations.FixedTimeEquals(hash, computed);
            }
            if (string.IsNullOrWhiteSpace(legacyKey)) return false;
            return CryptographicOperations.FixedTimeEquals(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(legacyKey)),
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(candidate)));
        }
    }

    public bool TrySet(string? oldKey, string? newKey)
    {
        if (string.IsNullOrWhiteSpace(newKey) || newKey.Length < 12 || newKey.Length > 256)
            throw new ArgumentException("Der neue AdminKey muss zwischen 12 und 256 Zeichen lang sein.");
        lock (gate)
        {
            if (Configured && !Verify(oldKey)) return false;
            var newSalt = RandomNumberGenerator.GetBytes(16);
            var newHash = Rfc2898DeriveBytes.Pbkdf2(newKey, newSalt, 210_000, HashAlgorithmName.SHA256, 32);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new KeyRecord(
                Convert.ToBase64String(newSalt), Convert.ToBase64String(newHash))));
            File.Move(temp, path, true);
            salt = newSalt;
            hash = newHash;
            legacyKey = null;
            return true;
        }
    }

    private sealed record KeyRecord(string Salt, string Hash);
}
