using System.Net;
using Knapper.Core.Options;

namespace Knapper.Mcp;

/// <summary>
/// DNS-rebinding / same-origin defense for the HTTP surface (ported from
/// Mailvec). The server binds loopback, which stops other HOSTS — but not a
/// browser on the same machine: a page can let its DNS re-resolve to
/// 127.0.0.1 and issue same-origin requests to this port, and stateless
/// Streamable HTTP answers a bare POST — one request reads vault notes out
/// of a tools/call response. Pin the Host header (and Origin when a browser
/// sends one) to an allowlist; after a rebind the browser still sends the
/// hostile hostname, so the request dies before any handler.
/// </summary>
public static class HostGuard
{
    // The configuration half — what "loopback" means, and when Access-off is
    // exposure — is Knapper.Core.Options.IngressConfiguration, so that
    // `knapper doctor` judges it with the server's own definition.
    internal static IReadOnlyList<string> Loopback => IngressConfiguration.LoopbackHosts;

    /// <inheritdoc cref="IngressConfiguration.IsLoopbackBind"/>
    public static bool IsLoopbackBind(string? bindAddress) => IngressConfiguration.IsLoopbackBind(bindAddress);

    /// <inheritdoc cref="IngressConfiguration.IsLoopbackHost"/>
    public static bool IsLoopbackHost(string? host) => IngressConfiguration.IsLoopbackHost(host);

    /// <summary>
    /// "Local" for security exemptions means BOTH the TCP peer and the
    /// requested Host are loopback. The peer alone is not identity:
    /// production fronts this server with cloudflared on the same box, so
    /// every tunneled internet request arrives from 127.0.0.1 — what
    /// distinguishes a genuine same-box caller is that it also ASKED for a
    /// loopback host. Tunneled requests keep their public hostname (never
    /// point cloudflared's httpHostHeader at a loopback name — the runbook
    /// pins this). Null peer = can't tell where it came from; fail closed.
    /// </summary>
    public static bool IsLocalRequest(Microsoft.AspNetCore.Http.HttpContext? context)
    {
        var remote = context?.Connection.RemoteIpAddress;
        return remote is not null && IPAddress.IsLoopback(remote)
            && IsLoopbackHost(context!.Request.Host.Host);
    }

    /// <inheritdoc cref="IngressConfiguration.UnauthenticatedExposureError"/>
    public static string? UnauthenticatedExposureError(McpOptions options) =>
        IngressConfiguration.UnauthenticatedExposureError(options);

    public static HashSet<string> BuildAllowedHosts(IEnumerable<string>? configured)
    {
        var set = new HashSet<string>(Loopback, StringComparer.OrdinalIgnoreCase);
        foreach (var host in configured ?? [])
        {
            var trimmed = IngressConfiguration.NormalizeHost(host?.Trim());
            if (!string.IsNullOrEmpty(trimmed))
                set.Add(trimmed);
        }
        return set;
    }

    /// <param name="host">Host header's host component, port stripped (HostString.Host).</param>
    /// <param name="origin">Raw Origin header, or null/empty when absent (native clients omit it).</param>
    /// <param name="allowedHosts">From <see cref="BuildAllowedHosts"/>.</param>
    public static bool IsAllowed(string? host, string? origin, HashSet<string> allowedHosts)
    {
        // Both sources keep an IPv6 literal's brackets; the set does not.
        host = IngressConfiguration.NormalizeHost(host);
        if (string.IsNullOrEmpty(host) || !allowedHosts.Contains(host))
            return false;
        if (!string.IsNullOrEmpty(origin))
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed))
                return false;
            if (!allowedHosts.Contains(IngressConfiguration.NormalizeHost(parsed.Host)!))
                return false;
        }
        return true;
    }
}
