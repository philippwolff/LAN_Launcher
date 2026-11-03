using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace LanLauncher.Client;

/// <summary>
/// Optional capability negotiation. Legacy Windows servers do not expose this endpoint
/// and must continue to use their existing HTTP download implementation.
/// </summary>
internal static class ServerTransferResolver
{
    internal const string CapabilityPath = "/api/capabilities";

    internal static async Task<TransferCapabilities> DetectAsync(
        HttpClient client, string server, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await client.GetAsync(
                server.TrimEnd('/') + CapabilityPath, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return TransferCapabilities.LegacyHttp;

            var capabilities = await response.Content.ReadFromJsonAsync<TransferCapabilities>(
                cancellationToken: cancellationToken);
            if (capabilities is { ProtocolVersion: 1, DownloadMode: "smb" }
                && TryValidateShare(capabilities.SmbShare))
                return capabilities;

            // Unknown modes and protocol versions are not safe to interpret as SMB.
            return TransferCapabilities.LegacyHttp;
        }
        catch (HttpRequestException)
        {
            return TransferCapabilities.LegacyHttp;
        }
        catch (System.Text.Json.JsonException)
        {
            return TransferCapabilities.LegacyHttp;
        }
    }

    internal static string ResolveSmbPackagePath(TransferCapabilities capabilities, string relativePath)
    {
        if (capabilities.DownloadMode != "smb" || !TryValidateShare(capabilities.SmbShare))
            throw new InvalidOperationException("Der Server bietet keinen gültigen SMB-Download an.");

        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new ArgumentException("Ungültiger relativer Paketpfad.", nameof(relativePath));

        var parts = relativePath.Replace('/', '\\').Split('\\');
        if (parts.Any(part => part.Length == 0 || part is "." or ".." || part.Contains(':')))
            throw new ArgumentException("Ungültiger relativer Paketpfad.", nameof(relativePath));

        return capabilities.SmbShare!.TrimEnd('\\') + "\\" + string.Join("\\", parts);
    }

    private static bool TryValidateShare(string? share)
    {
        if (string.IsNullOrWhiteSpace(share) || !share.StartsWith(@"\\", StringComparison.Ordinal))
            return false;
        var parts = share.Substring(2).Split('\\');
        return parts.Length >= 2 && parts.All(p => p.Length > 0 && p is not "." and not "..");
    }
}

internal sealed record TransferCapabilities(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("downloadMode")] string DownloadMode,
    [property: JsonPropertyName("smbShare")] string? SmbShare)
{
    internal static TransferCapabilities LegacyHttp { get; } = new(0, "http", null);
}
