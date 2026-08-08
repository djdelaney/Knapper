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
}
