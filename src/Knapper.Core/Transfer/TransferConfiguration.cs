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

    /// <summary>The upload tools, for the same rule: hiding any of them must not leave the PUT usable.</summary>
    public static readonly string[] UploadTools = ["vault_upload_prepare", "vault_upload_commit", "vault_upload_status"];

    private static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "::1"];

    public static IReadOnlyList<string> Validate(
        TransferOptions transfer,
        DownloadOptions download,
        UploadOptions upload,
        McpOptions mcp,
        long syncMaxFileBytes)
    {
        var problems = new List<string>();

        if (transfer.LocalOnly)
            problems.AddRange(LocalOnlyProblems(mcp));
        if (!download.Enabled && !upload.Enabled)
            return problems;

        problems.AddRange(OriginProblems(transfer, mcp));
        if (transfer.MaxBufferedBytes < syncMaxFileBytes)
        {
            problems.Add(
                $"Transfer:MaxBufferedBytes ({transfer.MaxBufferedBytes}) must hold at least one maximum-size file " +
                $"(Sync:MaxFileBytes, {syncMaxFileBytes}).");
        }
        var activeTotal = (download.Enabled ? download.MaxActiveGrants : 0) + (upload.Enabled ? upload.MaxActiveGrants : 0);
        if (transfer.MaxTrackedGrants < activeTotal)
        {
            problems.Add(
                $"Transfer:MaxTrackedGrants ({transfer.MaxTrackedGrants}) must cover the enabled directions' active " +
                $"grants ({activeTotal}).");
        }

        if (download.Enabled)
            problems.AddRange(DownloadProblems(download, mcp));
        if (upload.Enabled)
            problems.AddRange(UploadProblems(upload, mcp));
        return problems;
    }

    private static IEnumerable<string> DownloadProblems(DownloadOptions download, McpOptions mcp)
    {
        // Hiding a tool must never leave a usable route behind (§10), and a
        // read-only deployment hides vault_download_prepare because it is
        // marked a writer — on purpose (§13).
        if (mcp.ReadOnly)
        {
            yield return "Download:Enabled is true on a read-only deployment (Mcp:ReadOnly). vault_download_prepare is a " +
                         "writer by declaration — byte export is not safe on a restricted surface — so Mcp:ReadOnly hides " +
                         "it, and the GET route would be left half-enabled. Turn one of the two off.";
        }
        if (Hidden(mcp, DownloadTool))
        {
            yield return $"Download:Enabled is true but {DownloadTool} is in Mcp:DisabledTools — the GET route would be " +
                         "left usable with its tool hidden. Turn one of the two off.";
        }
        if (download.GrantLifetimeSeconds is < 10 or > 3600)
            yield return $"Download:GrantLifetimeSeconds must be 10–3600, got {download.GrantLifetimeSeconds}.";
        if (download.MaxActiveGrants < 1)
            yield return $"Download:MaxActiveGrants must be positive, got {download.MaxActiveGrants}.";
        if (download.MaxActiveGrantsPerPrincipal < 1 || download.MaxActiveGrantsPerPrincipal > download.MaxActiveGrants)
        {
            yield return $"Download:MaxActiveGrantsPerPrincipal must be 1–Download:MaxActiveGrants ({download.MaxActiveGrants}), " +
                         $"got {download.MaxActiveGrantsPerPrincipal}.";
        }
        string? prefixProblem = null;
        try
        {
            _ = new ArchivedPrefixes(download.AllowedPrefixes).Prefixes;
        }
        catch (Exception e) when (e is ArgumentException or KnapperException or InvalidOperationException)
        {
            prefixProblem = $"Download:AllowedPrefixes does not parse: {e.Message}";
        }
        if (prefixProblem is not null)
            yield return prefixProblem;
    }

    private static IEnumerable<string> UploadProblems(UploadOptions upload, McpOptions mcp)
    {
        if (mcp.ReadOnly)
        {
            yield return "Upload:Enabled is true on a read-only deployment (Mcp:ReadOnly), which hides vault_upload_commit — " +
                         "the PUT route would stage bytes nothing can commit. Turn one of the two off.";
        }
        var hidden = UploadTools.Where(t => Hidden(mcp, t)).ToList();
        if (hidden.Count > 0)
        {
            yield return $"Upload:Enabled is true but {string.Join(", ", hidden)} is in Mcp:DisabledTools — the upload " +
                         "direction would be left half-enabled. Turn one of the two off.";
        }
        if (upload.GrantLifetimeSeconds is < 30 or > 3600)
            yield return $"Upload:GrantLifetimeSeconds must be 30–3600, got {upload.GrantLifetimeSeconds}.";
        if (upload.ReceiveTimeoutSeconds < 5 || upload.ReceiveTimeoutSeconds > upload.GrantLifetimeSeconds)
        {
            yield return $"Upload:ReceiveTimeoutSeconds must be 5–Upload:GrantLifetimeSeconds ({upload.GrantLifetimeSeconds}), " +
                         $"got {upload.ReceiveTimeoutSeconds}.";
        }
        if (upload.MaxActiveGrants < 1)
            yield return $"Upload:MaxActiveGrants must be positive, got {upload.MaxActiveGrants}.";
        if (upload.MaxActiveGrantsPerPrincipal < 1 || upload.MaxActiveGrantsPerPrincipal > upload.MaxActiveGrants)
        {
            yield return $"Upload:MaxActiveGrantsPerPrincipal must be 1–Upload:MaxActiveGrants ({upload.MaxActiveGrants}), " +
                         $"got {upload.MaxActiveGrantsPerPrincipal}.";
        }
        if (upload.MaxConcurrentReceives < 1 || upload.MaxConcurrentReceives > upload.MaxActiveGrants)
        {
            yield return $"Upload:MaxConcurrentReceives must be 1–Upload:MaxActiveGrants ({upload.MaxActiveGrants}), " +
                         $"got {upload.MaxConcurrentReceives}.";
        }
        if (upload.ReceiptRetentionSeconds is < 60 or > 86400)
            yield return $"Upload:ReceiptRetentionSeconds must be 60–86400, got {upload.ReceiptRetentionSeconds}.";
    }

    private static bool Hidden(McpOptions mcp, string tool) =>
        (mcp.DisabledTools ?? []).Any(t => string.Equals(t?.Trim(), tool, StringComparison.OrdinalIgnoreCase));

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
        if (!System.Net.IPAddress.TryParse(mcp.BindAddress, out var bind) || !System.Net.IPAddress.IsLoopback(bind))
        {
            yield return $"Transfer:LocalOnly is refused while Mcp:BindAddress ('{mcp.BindAddress}') is not loopback — " +
                         "other machines can reach this port.";
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
