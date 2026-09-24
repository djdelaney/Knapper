using Knapper.Core.Generation;
using Knapper.Core.Options;
using Knapper.Core.Query;
using Knapper.Core.Vault;

namespace Knapper.Core.Tests.Query;

/// <summary>
/// One note that cannot be read is that note's problem, not the query's.
/// Batch read reports it per item; lint and frontmatter search let a raw I/O
/// failure on a single note escape and fail the whole-vault answer. They now
/// report it the way they report any note they could not examine.
///
/// Needs a non-root runner: root reads a mode-000 file regardless, so the
/// unreadable note is readable there (CI runs unprivileged).
/// </summary>
public sealed class UnreadableNoteQueryTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly VaultPathResolver _resolver;
    private readonly VaultOptions _options;
    private readonly VaultGenerationCounter _generation = new();
    private readonly VaultFileLister _lister;
    private readonly VaultReadService _reader;

    public UnreadableNoteQueryTests()
    {
        _dir.File("Notes/Real.md", "---\nstatus: active\n---\n[[Locked]]\n");
        var locked = _dir.File("Notes/Locked.md", "---\nstatus: active\n---\n# Locked\n");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        _resolver = new VaultPathResolver(_dir.Path);
        _options = new VaultOptions { RootPath = _resolver.Root };
        _lister = new VaultFileLister(_resolver, _generation, _options, ArchivedPrefixes.None);
        _reader = new VaultReadService(_resolver, _options, _generation);
    }

    public void Dispose()
    {
        File.SetUnixFileMode(Path.Combine(_dir.Path, "Notes/Locked.md"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _dir.Dispose();
    }

    [Fact]
    public void Lint_reports_an_unreadable_note_as_unexamined_and_still_answers()
    {
        var lint = new VaultLintService(_resolver, _lister, _reader, _generation, _options, ArchivedPrefixes.None);
        var result = lint.Lint(new LintQuery());
        result.UnexaminedFiles.ShouldBe(["Notes/Locked.md"]);
        // Still a valid link target: the link into it is not "unresolved".
        result.Items.ShouldNotContain(f => f.Path == "Notes/Real.md");
    }

    [Fact]
    public void Frontmatter_search_reports_an_unreadable_note_and_still_answers()
    {
        var search = new FrontmatterSearchService(_resolver, _lister, _reader, _generation, _options, ArchivedPrefixes.None);
        var result = search.Search(new FrontmatterQuery { Field = "status" });
        result.Items.ShouldHaveSingleItem().Path.ShouldBe("Notes/Real.md");
        result.UnparseableFiles.ShouldBe(["Notes/Locked.md"]);
    }
}
