using Knapper.Core.Options;
using Knapper.Core.Vault;

namespace Knapper.Core.Transfer;

/// <summary>
/// The ONE validator for transfer configuration, shared by startup and
/// <c>knapper doctor</c> so the two can never disagree about what a coherent
/// deployment is (docs/proposals/transfer-grants.md §10, §8.3, §13). Returns
/// every problem rather than the first, so an operator fixes them in one pass.
/// </summary>
public static class TransferConfiguration
{
    /// <summary>The download tool's name, for the half-enabled rule.</summary>
    public const string DownloadTool = "vault_download_prepare";

    private static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "::1"];

    public static IReadOnlyList<string> Validate(
        TransferOptions transfer,
        DownloadOptions download,
        UploadOptions upload,
        McpOptions mcp,
        long syncMaxFileBytes)
    {
        var problems = new List<string>();

        if (upload.Enabled)
        {
            problems.Add(
                "Upload:Enabled is true, but the upload direction is not built in this release — its tools are " +
                "registered only so the tool-name contract moves once. Set Upload:Enabled to false.");
        }

        if (transfer.LocalOnly)
            problems.AddRange(LocalOnlyProblems(mcp));

        if (!download.Enabled)
            return problems;

        // Hiding a tool must never leave a usable route behind (§10), and a
        // read-only deployment hides vault_download_prepare because it is
        // marked a writer — on purpose (§13).
        if (mcp.ReadOnly)
        {
            problems.Add(
                "Download:Enabled is true on a read-only deployment (Mcp:ReadOnly). vault_download_prepare is a " +
                "writer by declaration — byte export is not safe on a restricted surface — so Mcp:ReadOnly hides " +
                "it, and the GET route would be left half-enabled. Turn one of the two off.");
        }
        if ((mcp.DisabledTools ?? []).Any(t => string.Equals(t?.Trim(), DownloadTool, StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add(
                $"Download:Enabled is true but {DownloadTool} is in Mcp:DisabledTools — the GET route would be " +
                "left usable with its tool hidden. Turn one of the two off.");
        }

        problems.AddRange(OriginProblems(transfer, mcp));

        if (download.GrantLifetimeSeconds is < 10 or > 3600)
            problems.Add($"Download:GrantLifetimeSeconds must be 10–3600, got {download.GrantLifetimeSeconds}.");
        if (download.MaxActiveGrants < 1)
            problems.Add($"Download:MaxActiveGrants must be positive, got {download.MaxActiveGrants}.");
        if (download.MaxActiveGrantsPerPrincipal < 1 || download.MaxActiveGrantsPerPrincipal > download.MaxActiveGrants)
        {
            problems.Add(
                $"Download:MaxActiveGrantsPerPrincipal must be 1–Download:MaxActiveGrants ({download.MaxActiveGrants}), " +
                $"got {download.MaxActiveGrantsPerPrincipal}.");
        }
        if (transfer.MaxTrackedGrants < download.MaxActiveGrants)
        {
            problems.Add(
                $"Transfer:MaxTrackedGrants ({transfer.MaxTrackedGrants}) must cover Download:MaxActiveGrants " +
                $"({download.MaxActiveGrants}).");
        }
        if (transfer.MaxBufferedBytes < syncMaxFileBytes)
        {
            problems.Add(
                $"Transfer:MaxBufferedBytes ({transfer.MaxBufferedBytes}) must hold at least one maximum-size file " +
                $"(Sync:MaxFileBytes, {syncMaxFileBytes}).");
        }

        try
        {
            var allowed = new ArchivedPrefixes(download.AllowedPrefixes);
            _ = allowed.Prefixes;
        }
        catch (Exception e) when (e is ArgumentException or KnapperException or InvalidOperationException)
        {
            problems.Add($"Download:AllowedPrefixes does not parse: {e.Message}");
        }

        return problems;
    }

    private static IEnumerable<string> LocalOnlyProblems(McpOptions mcp)
    {
        // Only the configuration that already means "this machine only" may
        // mint the local principal: everywhere else cloudflared delivers
        // internet traffic from a loopback PEER, the confusion HostGuard
        // exists to prevent.
        if (mcp.Access.Enabled)
            yield return "Transfer:LocalOnly requires Mcp:Access:Enabled false — an Access deployment has real principals.";
        if (mcp.Access.AllowPublicHostsWithoutAccess)
        {
            yield return "Transfer:LocalOnly is refused with Mcp:Access:AllowPublicHostsWithoutAccess — that flag means " +
                         "an edge fronts this server.";
        }
        var publicHosts = (mcp.AllowedHosts ?? [])
            .Select(h => h?.Trim())
            .Where(h => !string.IsNullOrEmpty(h) && !IsLoopbackName(h))
            .ToList();
        if (publicHosts.Count > 0)
        {
            yield return $"Transfer:LocalOnly is refused while Mcp:AllowedHosts names a non-loopback host " +
                         $"({string.Join(", ", publicHosts)}).";
        }
    }

    private static IEnumerable<string> OriginProblems(TransferOptions transfer, McpOptions mcp)
    {
        if (string.IsNullOrWhiteSpace(transfer.PublicOrigin))
        {
            yield return "Transfer:PublicOrigin is required when a transfer direction is enabled — grant URLs are " +
                         "built from it, never from request headers.";
            yield break;
        }
        if (!Uri.TryCreate(transfer.PublicOrigin, UriKind.Absolute, out var origin))
        {
            yield return $"Transfer:PublicOrigin '{transfer.PublicOrigin}' is not an absolute URL.";
            yield break;
        }
        if (!string.IsNullOrEmpty(origin.UserInfo))
            yield return "Transfer:PublicOrigin must not carry userinfo.";
        if (origin.AbsolutePath != "/" || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
            yield return "Transfer:PublicOrigin must be an origin only: no path, query or fragment.";

        var loopbackOrigin = IsLoopbackName(origin.DnsSafeHost);
        if (transfer.LocalOnly)
        {
            if (!loopbackOrigin)
                yield return "Transfer:LocalOnly requires a loopback Transfer:PublicOrigin (localhost, 127.0.0.1 or [::1]).";
            if (origin.Scheme is not ("http" or "https"))
                yield return "Transfer:PublicOrigin must be http or https.";
        }
        else
        {
            if (origin.Scheme != "https")
                yield return "Transfer:PublicOrigin must be https (plain http is allowed only in Transfer:LocalOnly mode).";
            if (!loopbackOrigin && !(mcp.AllowedHosts ?? []).Any(h =>
                    string.Equals(h?.Trim(), origin.DnsSafeHost, StringComparison.OrdinalIgnoreCase)))
            {
                yield return $"Transfer:PublicOrigin's host '{origin.DnsSafeHost}' is not in Mcp:AllowedHosts — " +
                             "HostGuard would refuse every transfer request.";
            }
        }
    }

    private static bool IsLoopbackName(string? host) =>
        !string.IsNullOrEmpty(host) && LoopbackHosts.Contains(host.Trim('[', ']'), StringComparer.OrdinalIgnoreCase);
}
