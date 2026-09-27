namespace Knapper.Core.Options;

/// <summary>
/// Lint's deployment knobs (docs/proposals/vault-lint.md §9). Deployment
/// state, like every other section: nothing here is read from the vault.
/// </summary>
public sealed class LintOptions
{
    public const string SectionName = "Lint";

    /// <summary>
    /// Where the accepted lint BASELINE is recorded: the commit whose findings
    /// are the standing backlog, so <c>vault_lint</c> reports only what is
    /// absent from it (§5). Written ONLY by <c>knapper lint --accept</c> — a
    /// run never advances it. Empty = no baseline, and every finding is
    /// reported, exactly as before this setting existed.
    ///
    /// <para>OUTSIDE the vault, enforced at startup and by <c>knapper
    /// doctor</c>, and for more than the usual "operational files must never
    /// sync" reason: the baseline decides which findings are SILENCED, so it
    /// must be out of reach of the agents lint reports on (every vault path is
    /// writable through the tools) and of obsidian-headless (which can write
    /// <c>.git/</c> — the reason this is not a git ref).</para>
    /// </summary>
    public string BaselinePath { get; set; } = "";
}
