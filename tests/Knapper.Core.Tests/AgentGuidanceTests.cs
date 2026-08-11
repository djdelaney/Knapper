namespace Knapper.Core.Tests;

/// <summary>
/// AGENTS.md and CLAUDE.md are one set of invariants under the two names
/// different coding agents look for. They are two REAL byte-identical files
/// rather than a symlink, because agent tooling in use here refuses to read
/// symlinked instructions — and a tool that cannot read the file gets no
/// guidance at all, without saying so.
///
/// The cost of that choice is that drift becomes possible, and drift is
/// silent by construction: nothing errors, each agent just reads
/// plausible-looking guidance while the two copies say different things.
/// This test and the CI job are what make it loud.
/// </summary>
public sealed class AgentGuidanceTests
{
    [Fact]
    public void AGENTS_md_and_CLAUDE_md_carry_identical_guidance()
    {
        if (RepoRoot() is not { } root)
            return; // not a source checkout (published artifact) — nothing to check

        var claude = Path.Combine(root, "CLAUDE.md");
        var agents = Path.Combine(root, "AGENTS.md");

        File.Exists(claude).ShouldBeTrue("CLAUDE.md is missing");
        File.Exists(agents).ShouldBeTrue(
            "AGENTS.md is missing — agents that read that name get no guidance at all. " +
            "Restore with: cp CLAUDE.md AGENTS.md");

        // Deliberately NOT a symlink: tooling in use here refuses to read one,
        // and does so quietly. Re-linking would look like tidying up duplication
        // and would take that tool's guidance away without any visible failure.
        new FileInfo(agents).LinkTarget.ShouldBeNull(
            "AGENTS.md is a symlink. It must be a real file — agent tooling used here refuses " +
            "symlinked instructions and then runs with no guidance at all. " +
            "Fix with: rm AGENTS.md && cp CLAUDE.md AGENTS.md");

        File.ReadAllText(agents).ShouldBe(
            File.ReadAllText(claude),
            "AGENTS.md and CLAUDE.md have drifted. Edit CLAUDE.md, then mirror it with: " +
            "cp CLAUDE.md AGENTS.md");
    }

    /// <summary>Walks up from the test binary to the checkout; null when there isn't one.</summary>
    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Knapper.slnx")))
            dir = dir.Parent;
        return dir?.FullName;
    }
}
