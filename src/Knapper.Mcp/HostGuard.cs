using System.Net;

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
    internal static readonly string[] Loopback = ["localhost", "127.0.0.1", "::1"];

    /// <summary>Unparseable counts as non-loopback — "couldn't tell" must not resolve to "safe".</summary>
    public static bool IsLoopbackBind(string? bindAddress) =>
        IPAddress.TryParse(bindAddress, out var ip) && IPAddress.IsLoopback(ip);

    public static HashSet<string> BuildAllowedHosts(IEnumerable<string>? configured)
    {
        var set = new HashSet<string>(Loopback, StringComparer.OrdinalIgnoreCase);
        foreach (var host in configured ?? [])
        {
            var trimmed = host?.Trim();
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
        if (string.IsNullOrEmpty(host) || !allowedHosts.Contains(host))
            return false;
        if (!string.IsNullOrEmpty(origin))
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed))
                return false;
            if (!allowedHosts.Contains(parsed.Host))
                return false;
        }
        return true;
    }
}
