namespace Knapper.Core.Options;

/// <summary>
/// Settings shared by both transfer directions
/// (docs/proposals/transfer-grants.md §10). Fixed for the life of the
/// process: a change needs a restart, which invalidates every grant.
/// </summary>
public sealed class TransferOptions
{
    public const string SectionName = "Transfer";

    /// <summary>
    /// The one path prefix both transfer routes live under. A CONSTANT, not a
    /// setting: the edge's path-scoped Bypass application must name the same
    /// string, and a configurable prefix would be a second carrier free to
    /// disagree with it. Everything under it reaches the origin
    /// unauthenticated, so the origin owns the whole prefix (a catch-all
    /// answers 404 for anything that is not one of the two routes).
    /// </summary>
    public const string PathPrefix = "/transfer";

    /// <summary>
    /// The exact origin grant URLs are built from — scheme, host and optional
    /// port, nothing else. Never derived from Host or forwarded headers.
    /// Required when either direction is enabled.
    /// </summary>
    public string PublicOrigin { get; set; } = "";

    /// <summary>
    /// Same-machine deployments without Cloudflare Access (§8.3). Mints the
    /// fixed local principal and allows a loopback http origin; startup
    /// refuses it unless the configuration already means "this machine only".
    /// </summary>
    public bool LocalOnly { get; set; }

    /// <summary>Shared byte budget for download snapshots (and, once built, upload buffers).</summary>
    public long MaxBufferedBytes { get; set; } = 32 * 1024 * 1024;

    /// <summary>Active plus retained terminal grant records, both directions.</summary>
    public int MaxTrackedGrants { get; set; } = 128;
}

/// <summary>The download direction (§5). Off by default.</summary>
public sealed class DownloadOptions
{
    public const string SectionName = "Download";

    /// <summary>Enables <c>vault_download_prepare</c> and <c>GET /transfer/downloads/*</c> together.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Vault-relative prefixes downloads may serve. EMPTY MEANS NOTHING —
    /// fail closed. Matched with <c>ArchivedPrefixes.Covers</c>' ordinal,
    /// boundary-aware rule (the ONE predicate). Operator configuration, never
    /// vault content: a rule stored in the vault can be switched off by the
    /// agents it constrains.
    /// </summary>
    public string[] AllowedPrefixes { get; set; } = [];

    /// <summary>Issued-to-served lifetime. Short: a grant is meant for immediate use.</summary>
    public int GrantLifetimeSeconds { get; set; } = 300;

    public int MaxActiveGrants { get; set; } = 16;

    public int MaxActiveGrantsPerPrincipal { get; set; } = 16;
}

/// <summary>
/// The upload direction (§4). NOT BUILT in this release: its three tools are
/// registered so the tool-name contract moves once, and answer
/// <c>UploadDisabled</c>. Turning it on refuses startup rather than enabling
/// nothing.
/// </summary>
public sealed class UploadOptions
{
    public const string SectionName = "Upload";

    public bool Enabled { get; set; }
}
