namespace Knapper.Core.Tests;

/// <summary>
/// Nothing may read a file's creation (birth) time, on any surface.
///
/// `ob sync` restores mtime but not btime: the package ships btime-setting
/// binaries for darwin and win32 only, because Linux has no API to set a
/// file's creation time. So on CT 106 every synced note is born at download
/// time while its mtime stays correct — measured 2026-08-12, birth 23:00:56
/// (the download) against mtime 18:53:13 (the source edit).
///
/// Today no code reads it, which is why the gap is cosmetic. The reason this
/// is a source check rather than a behavioral test is that no behavioral test
/// could catch the regression: a test creates its own file, so the btime it
/// reads back is the one it just caused, and it matches on every platform. A
/// `created` field or a `createdAfter` filter would therefore go green in CI
/// and on Dan's Macs, and be wrong only against the real synced vault — where
/// it would report download dates as authorship dates, plausibly and without
/// any error. Grepping the source is the only place this is visible.
///
/// If a creation-time field is ever genuinely wanted, the answer is vault
/// content (frontmatter), not filesystem metadata — content survives sync.
/// </summary>
public sealed class CreationTimeTests
{
    /// <summary>
    /// Managed reads (`CreationTime`, `CreationTimeUtc`, `NotifyFilters.CreationTime`)
    /// and the raw stat fields a P/Invoke would reach for. `watcher.Created` — an
    /// event kind, not a timestamp — deliberately does not match.
    /// </summary>
    private static readonly string[] Banned =
        ["CreationTime", "birthtime", "st_birthtime", "stx_btime"];

    [Fact]
    public void No_surface_reads_file_creation_time()
    {
        if (RepoRoot() is not { } root)
            return; // not a source checkout (published artifact) — nothing to check

        var src = Path.Combine(root, "src");
        Directory.Exists(src).ShouldBeTrue("src/ is missing");

        var offenders = Directory
            .EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => File.ReadAllLines(f)
                .Select((line, i) => (File: Path.GetRelativePath(root, f), No: i + 1, Text: line))
                .Where(l => Banned.Any(b => l.Text.Contains(b, StringComparison.Ordinal))))
            .Select(l => $"{l.File}:{l.No}: {l.Text.Trim()}")
            .ToList();

        offenders.ShouldBeEmpty(
            "Something now reads file creation time. Birth time is NOT preserved by sync on " +
            "Linux (no API exists to set it), so on CT 106 this reads the download time while " +
            "mtime stays correct — it will look right in dev and on every test, and be wrong " +
            "only in production. Use LastWriteTimeUtc, as every other surface does, or carry " +
            "the date in frontmatter. Rationale: docs/extending.md, closed decisions.\n" +
            string.Join("\n", offenders));
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
