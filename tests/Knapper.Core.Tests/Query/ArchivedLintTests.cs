using Knapper.Core.Generation;
using Knapper.Core.Options;
using Knapper.Core.Query;
using Knapper.Core.Vault;

namespace Knapper.Core.Tests.Query;

/// <summary>
/// Archived subtrees drop out of lint's REPORTING and stay in its INDEX.
///
/// <para>The distinction is the whole test. Lint's index is deliberately
/// whole-vault even when a PathPrefix narrows the report, because a link
/// inside the scope can point anywhere; an archived note is an ordinary link
/// TARGET. Excluding archived notes from the index as well would turn every
/// link into the archive into a false <c>unresolved_link</c> — the check
/// reporting damage that does not exist, on a vault that is fine, in numbers
/// that grow with the archive.</para>
/// </summary>
public sealed class ArchivedLintTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly VaultGenerationCounter _generation = new();
    private readonly VaultLintService _lint;

    public ArchivedLintTests()
    {
        // A live note pointing INTO the archive: resolvable only if archived
        // notes remain in the index.
        _dir.File("Notes/Live.md", "See [[Old]] for the history.\nAlso [[Nowhere]].\n");
        _dir.File("Archive/Old.md", "The superseded version.\nIt links to [[AlsoNowhere]].\n");

        var resolver = new VaultPathResolver(_dir.Path);
        var options = new VaultOptions { RootPath = resolver.Root };
        var archived = new ArchivedPrefixes(["Archive"]);
        var lister = new VaultFileLister(resolver, _generation, options, archived);
        var reader = new VaultReadService(resolver, options, _generation);
        _lint = new VaultLintService(resolver, lister, reader, _generation, options, archived);
    }

    [Fact]
    public void An_archived_note_is_a_valid_link_target_but_not_a_reported_file()
    {
        var result = _lint.Lint(new LintQuery { Checks = [LintChecks.UnresolvedLink] });

        // [[Old]] resolves — the archived note is still indexed.
        // [[AlsoNowhere]] is inside the archive, so it is not reported.
        result.Items.Select(f => (f.Path, f.Subject))
            .ShouldBe([("Notes/Live.md", "Nowhere")]);
        result.ExcludedPrefixes.ShouldBe(["Archive"]);
        result.Truncated.ShouldBeFalse();
    }

    [Fact]
    public void Scoping_the_lint_to_the_archive_reports_on_it()
    {
        var result = _lint.Lint(new LintQuery
        {
            PathPrefix = "Archive",
            Checks = [LintChecks.UnresolvedLink],
        });

        result.Items.Select(f => f.Path).ShouldBe(["Archive/Old.md"]);
        result.ExcludedPrefixes.ShouldBeEmpty();
    }

    public void Dispose()
    {
        _generation.Dispose();
        _dir.Dispose();
    }
}
