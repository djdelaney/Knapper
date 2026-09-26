namespace Knapper.Core.Options;

/// <summary>
/// The vault service's knobs. Bound from configuration by each executable;
/// every budget here has protocol semantics — hitting one produces a typed
/// error or an explicit <c>truncated</c> + cursor, never silent partial
/// success (brief §8).
/// </summary>
public sealed class VaultOptions
{
    public const string SectionName = "Vault";

    /// <summary>Absolute path of the vault root on this machine.</summary>
    public string RootPath { get; set; } = "";

    /// <summary>Advisory-lock directory — OUTSIDE the vault; lock files must never sync.</summary>
    public string LockDirectory { get; set; } = "";

    /// <summary>ripgrep binary. Pinned via apt in production; never floats past `doctor` unchecked.</summary>
    public string RipgrepPath { get; set; } = "rg";

    /// <summary>Wall-clock budget for one search/list/frontmatter query.</summary>
    public int QueryTimeoutMs { get; set; } = 10_000;

    /// <summary>Hard page-size ceiling; a query's own max_results is clamped to this.</summary>
    public int MaxResultsPerPage { get; set; } = 200;

    /// <summary>Result-payload byte budget per search page (match text; not protocol overhead).</summary>
    public int MaxOutputBytes { get; set; } = 1_000_000;

    /// <summary>Whole-file read cap. Beyond it reads fail TooLarge — explicitly, never truncated.</summary>
    public int MaxReadBytes { get; set; } = 4_000_000;

    /// <summary>Max paths per vault_batch_read request.</summary>
    public int MaxBatchItems { get; set; } = 50;

    /// <summary>How long a mutation waits for its advisory locks before failing LockTimeout.</summary>
    public int LockTimeoutMs { get; set; } = 10_000;

    /// <summary>
    /// Wall-clock budget for EACH vault walk on the health path — the
    /// conflict-file scan (uncached, every /health and /up) and the
    /// oversized-file scan — and for doctor's oversized scan. A walk that
    /// exceeds it reports "could not tell" and degrades health; that state
    /// does not clear while the vault stays that large, so this is the lever
    /// (it was a compiled-in 5 s until 0.11.1).
    ///
    /// <para>Capped at <see cref="MaxHealthScanBudgetMs"/> because a single
    /// /up can run BOTH walks plus the 5 s ripgrep probe, and the external
    /// monitor gives /up 20 s (CURL_TIMEOUT): 2 × 7 s + 5 s stays under it. A
    /// larger budget would turn a slow walk into a monitor timeout instead of
    /// a reported degradation. Validated at startup.</para>
    /// </summary>
    public int HealthScanBudgetMs { get; set; } = 5_000;

    public const int MinHealthScanBudgetMs = 500;
    public const int MaxHealthScanBudgetMs = 7_000;

    /// <summary>Null when valid; otherwise the reason startup refuses it.</summary>
    public static string? ValidateHealthScanBudget(int ms) =>
        ms is >= MinHealthScanBudgetMs and <= MaxHealthScanBudgetMs
            ? null
            : $"Vault:HealthScanBudgetMs is {ms}; it must be {MinHealthScanBudgetMs}–{MaxHealthScanBudgetMs} " +
              "(each /up may run two walks plus a 5 s ripgrep probe inside the monitor's 20 s timeout)";

    /// <summary>
    /// Vault subtrees holding superseded copies: skipped by every query
    /// unless the caller names one, and immutable once written (create and
    /// move-in stay legal — that is how an archive is filled). Vault-relative,
    /// no leading slash, e.g. <c>Archive</c>. Empty = the feature is off.
    ///
    /// <para>This is CONFIGURATION rather than a marker in the vault because
    /// the rule constrains agents, and an agent can write any vault path.
    /// Validated at boot: a malformed entry refuses startup rather than
    /// silently protecting a subtree nobody named.</para>
    /// </summary>
    public string[] ArchivedPrefixes { get; set; } = [];

    /// <summary>Append-only JSONL audit log — OUTSIDE the vault, always.</summary>
    public string AuditLogPath { get; set; } = "";

    /// <summary>
    /// Durably touched by `knapper commit` on every SUCCESSFUL run,
    /// including "nothing to commit" — the external monitor's git-freshness
    /// signal (last-commit age can't tell a quiet vault from a dead timer).
    /// Empty = no stamp. Outside the vault, like every operational file
    /// (enforced by the commit job and `knapper doctor`).
    /// </summary>
    public string CommitStampPath { get; set; } = "";

    /// <summary>
    /// The bounded metrics snapshot the external monitor reads (brief §8:
    /// query timeout/error/truncation rates, generation-changed responses,
    /// stale-write rejections, audit-append failures). Empty = counters stay
    /// in memory only. Outside the vault, like every operational file
    /// (enforced at startup).
    /// </summary>
    public string MetricsPath { get; set; } = "";
}
