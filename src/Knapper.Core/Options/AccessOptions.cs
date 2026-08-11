namespace Knapper.Core.Options;

/// <summary>
/// Origin-side validation of Cloudflare Access's <c>Cf-Access-Jwt-Assertion</c>
/// (ported from Mailvec — the proven B2 ingress). With the tunnel as the only
/// network path, Access at the edge is the auth gate; this validates the
/// edge's assertion AT THE ORIGIN so a tunnel misconfiguration can't silently
/// expose the vault.
/// </summary>
public sealed class AccessOptions
{
    /// <summary>Master switch. When false, no authentication middleware is registered at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The Zero Trust team domain, e.g. <c>https://myteam.cloudflareaccess.com</c> —
    /// WITH the scheme (compared verbatim against the <c>iss</c> claim).
    /// </summary>
    public string TeamDomain { get; set; } = "";

    /// <summary>AUD tag of the Access application fronting the vault surface. Required when enabled.</summary>
    public string Audience { get; set; } = "";

    /// <summary>
    /// AUD tag of a separate path-scoped Access app fronting <c>/up</c> for an
    /// external monitor. Accepted on <c>/up</c> ONLY — a leaked monitoring
    /// credential must never reach the vault; the asymmetry is the point.
    /// </summary>
    public string MonitoringAudience { get; set; } = "";

    /// <summary>
    /// Exempt genuinely local callers (health checks, doctor probes on the
    /// same box). Local requires BOTH a loopback TCP peer AND a loopback Host
    /// header: cloudflared proxies every tunneled internet request from
    /// 127.0.0.1, so the peer alone must never satisfy authorization — a
    /// tunneled request keeps its public hostname and is validated normally.
    /// </summary>
    public bool AllowLoopback { get; set; } = true;

    /// <summary>
    /// The JWKS Access signs with — a bare key set, NOT an OIDC discovery
    /// document. Access publishes no discovery document at the team domain;
    /// deriving a MetadataAddress 404s silently and the server authenticates
    /// nobody (failure already paid for in Mailvec — see its AccessOptions).
    /// </summary>
    public string CertsAddress => $"{TeamDomain.TrimEnd('/')}/cdn-cgi/access/certs";

    public string[] AllAudiences() =>
        string.IsNullOrWhiteSpace(MonitoringAudience) ? [Audience] : [Audience, MonitoringAudience];

    public string[] OwnerAudiences() => [Audience];

    public string[] MonitoringAudiences() => AllAudiences();

    /// <summary>Null when coherent; an error message when the server must refuse to start.</summary>
    public string? Validate()
    {
        if (!Enabled)
            return null;
        if (string.IsNullOrWhiteSpace(TeamDomain))
            return "Mcp:Access:Enabled is true but Mcp:Access:TeamDomain is empty.";
        if (!Uri.TryCreate(TeamDomain, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return $"Mcp:Access:TeamDomain must be an absolute https:// URL, got '{TeamDomain}'.";
        if (string.IsNullOrWhiteSpace(Audience))
            return "Mcp:Access:Enabled is true but Mcp:Access:Audience is empty — any token from the team would authenticate.";
        // Equal audiences silently collapse the asymmetry documented on
        // MonitoringAudience: the monitoring token satisfies OwnerAudiences()
        // too, so a leaked monitoring credential reaches the whole vault while
        // the config still reads like a path-scoped restriction.
        if (string.Equals(MonitoringAudience, Audience, StringComparison.Ordinal))
            return "Mcp:Access:MonitoringAudience equals Mcp:Access:Audience — the monitoring credential "
                 + "would carry the whole vault surface, not just /up. Use a separate path-scoped Access "
                 + "application, or leave MonitoringAudience empty.";
        return null;
    }
}
