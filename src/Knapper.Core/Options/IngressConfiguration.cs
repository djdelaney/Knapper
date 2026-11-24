using System.Net;

namespace Knapper.Core.Options;

/// <summary>
/// The configuration half of the ingress posture: what makes a bind or a
/// Host name "loopback", and when a configuration with Access off is
/// exposure rather than a dev setup. It lives in Core for the reason
/// <c>TransferConfiguration</c> does — ONE definition for the server's
/// startup refusal and <c>knapper doctor</c>. While it lived in the MCP host,
/// doctor restated a weaker version and printed "ok Ingress" over
/// configurations the server refuses to boot on (security review
/// 2026-11-24). The request-time half stays in <c>HostGuard</c>.
/// </summary>
public static class IngressConfiguration
{
    public static readonly IReadOnlyList<string> LoopbackHosts = ["localhost", "127.0.0.1", "::1"];

    /// <summary>Unparseable counts as non-loopback — "couldn't tell" must not resolve to "safe".</summary>
    public static bool IsLoopbackBind(string? bindAddress) =>
        IPAddress.TryParse(bindAddress, out var ip) && IPAddress.IsLoopback(ip);

    /// <summary>
    /// A host as the allowlists spell it: a bracketed IPv6 literal loses its
    /// brackets, everything else is returned untouched. Both
    /// <c>HostString.Host</c> and <c>Uri.Host</c> KEEP the brackets
    /// (<c>[::1]</c>), while the lists hold <c>::1</c> — so an IPv6-loopback
    /// deployment that <c>TransferConfiguration</c> accepts and names in its
    /// own error text passed startup and then 403'd every request (security
    /// review 2026-11-24). Brackets come off ONLY around something that
    /// parses as an IPv6 address: <c>[localhost]</c> or <c>[127.0.0.1]</c>
    /// is not a spelling of a loopback name and must not become one.
    /// </summary>
    public static string? NormalizeHost(string? host) =>
        host is { Length: > 2 } && host[0] == '[' && host[^1] == ']'
            && IPAddress.TryParse(host.AsSpan(1, host.Length - 2), out var ip)
            && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? host[1..^1]
            : host;

    /// <summary>The Host-header host component names this box, not a public hostname.</summary>
    public static bool IsLoopbackHost(string? host) =>
        !string.IsNullOrEmpty(host)
        && LoopbackHosts.Contains(NormalizeHost(host), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Null when coherent; the refusal when Access is off and either the bind
    /// is not loopback or a configured allowed host is public. A public name in AllowedHosts exists for one
    /// reason — requests arriving under it through the tunnel — so pairing it
    /// with disabled origin validation is exposure, not a dev setup. Refusing
    /// here, rather than warning, is what makes the runbook's step order safe:
    /// a tunnel routed before the Access apps exist reaches a server that
    /// 403s the public Host, instead of one that serves it.
    /// </summary>
    public static string? UnauthenticatedExposureError(McpOptions options)
    {
        if (options.Access.Enabled || options.Access.AllowPublicHostsWithoutAccess)
            return null;
        // The Host allowlist is a BROWSER defence: a browser cannot forge Host,
        // but any other client can. On a non-loopback bind, `Host: localhost`
        // from another machine passes it, and with Access off nothing else
        // stands between that machine and the whole vault (security review
        // 2026-11: one forged header listed a vault over the LAN).
        if (!IsLoopbackBind(options.BindAddress))
        {
            return $"Mcp:BindAddress '{options.BindAddress}' is not loopback but Mcp:Access:Enabled is false — " +
                   "any machine that can reach this port could read and MUTATE the whole vault by sending " +
                   "'Host: localhost'. Bind 127.0.0.1, or enable Access (runbook §6.3).";
        }
        var publicHosts = (options.AllowedHosts ?? [])
            .Select(h => h?.Trim())
            .Where(h => !string.IsNullOrEmpty(h) && !IsLoopbackHost(h))
            .ToList();
        if (publicHosts.Count == 0)
            return null;
        return $"Mcp:AllowedHosts names a public hostname ({string.Join(", ", publicHosts)}) but " +
               "Mcp:Access:Enabled is false — anything reaching this server under that name could read " +
               "and MUTATE the whole vault. Enable Access (runbook §6.3), or remove the public hostname " +
               "until you do.";
    }

    /// <summary>
    /// Everything the server's startup refuses about ingress, in the order it
    /// checks: Access misconfiguration, then unauthenticated exposure. Null
    /// when it would start.
    /// </summary>
    public static string? StartupError(McpOptions options) =>
        options.Access.Validate() ?? UnauthenticatedExposureError(options);
}
