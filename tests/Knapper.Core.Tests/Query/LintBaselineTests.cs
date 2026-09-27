using Knapper.Core.Git;
using Knapper.Core.Options;
using Knapper.Core.Query;
using Knapper.Core.Tests.Mutation;
using Knapper.Core.Vault;

namespace Knapper.Core.Tests.Query;

/// <summary>
/// Proposal §5: findings are reported relative to an ACCEPTED baseline
/// commit, re-linted from its git tree. Every test runs against a real
/// repository made by the real committer, because the baseline side of the
/// diff is only as right as the tree it reads.
/// </summary>
public sealed class LintBaselineTests : IDisposable
{
    private readonly MutationVault _v = new();
    private readonly GitCommitJob _git;
    private readonly string _baselinePath;

    public LintBaselineTests()
    {
        _git = new GitCommitJob(_v.Resolver, _v.Locks);
        _baselinePath = Path.Combine(_v.Outside.Path, "state", "lint-baseline.json");
        _git.Init();
        _v.Write("Notes/Hub.md", string.Join('\n',
        [
            "# Hub",
            "Backlog: [[No Such Note]] and [[Target#Missing Heading]].",
            "Fine: [[Target]].",
            "",
        ]));
        _v.Write("Reference/Target.md", "# Target\n## Real Heading\n");
        _v.Write("Other/Elsewhere.md", "[[Also Missing]]\n");
    }

    public void Dispose() => _v.Dispose();

    private VaultLintService Service(string? baselinePath = null, VaultOptions? options = null)
    {
        var o = options ?? _v.Options;
        var lister = new VaultFileLister(_v.Resolver, _v.Generation, o, ArchivedPrefixes.None);
        var reader = new VaultReadService(_v.Resolver, o, _v.Generation);
        return new VaultLintService(_v.Resolver, lister, reader, _v.Generation, o, ArchivedPrefixes.None,
            new LintBaselineStore(baselinePath ?? _baselinePath));
    }

    private LintAcceptance CommitAndAccept(VaultLintService? lint = null)
    {
        _git.Commit(TimeSpan.FromSeconds(10));
        return (lint ?? Service()).Accept(DateTimeOffset.UtcNow);
    }

    private static List<string> Subjects(LintResult r) => [.. r.Items.Select(f => $"{f.Check}:{f.Path}:{f.Subject}")];

    [Fact]
    public void With_no_baseline_every_finding_is_reported_and_the_response_says_so()
    {
        foreach (var lint in (VaultLintService[])[Service(baselinePath: ""), Service()])
        {
            var r = lint.Lint(new LintQuery());
            r.Items.Count.ShouldBe(3);
            r.BaselineCommit.ShouldBeNull("null is what tells the caller nothing was suppressed");
            r.BaselineAcceptedAt.ShouldBeNull();
            r.SuppressedByBaseline.ShouldBe(0);
        }
    }

    [Fact]
    public void An_accepted_backlog_is_suppressed_and_counted_never_hidden()
    {
        var accepted = CommitAndAccept();
        accepted.AcceptedFindings.ShouldBe(3);
        accepted.PreviousCommit.ShouldBeNull();
        accepted.ByCheck[LintChecks.UnresolvedLink].ShouldBe(2);
        accepted.ByCheck[LintChecks.BrokenAnchor].ShouldBe(1);

        var r = Service().Lint(new LintQuery());
        r.Items.ShouldBeEmpty();
        r.Truncated.ShouldBeFalse();
        r.TotalMatches.ShouldBe(0);
        r.SuppressedByBaseline.ShouldBe(3);
        r.BaselineCommit.ShouldBe(accepted.Commit);
        r.BaselineAcceptedAt.ShouldNotBeNull();
    }

    [Fact]
    public void Only_what_changed_since_the_baseline_is_reported_including_uncommitted_edits()
    {
        CommitAndAccept();
        // Not committed: lint reads the WORKING tree against the baseline, so
        // "did the last session break something?" is answered before the
        // commit timer has run.
        _v.Write("Notes/New.md", "Fresh: [[Brand New Missing]].\n");

        var r = Service().Lint(new LintQuery());
        Subjects(r).ShouldBe(["unresolved_link:Notes/New.md:Brand New Missing"]);
        r.SuppressedByBaseline.ShouldBe(3);
    }

    [Fact]
    public void Moving_findings_down_the_page_is_not_a_change()
    {
        // The silent failure a line-keyed baseline has: one inserted paragraph
        // re-reports every finding below it, a flood that looks exactly like
        // real drift and is produced by an edit that broke nothing.
        CommitAndAccept();
        var hub = File.ReadAllText(Path.Combine(_v.VaultDir.Path, "Notes/Hub.md"));
        _v.Write("Notes/Hub.md", hub.Replace("# Hub\n", "# Hub\n\nA new paragraph.\n\nAnd another.\n\n"));

        var r = Service().Lint(new LintQuery());
        r.Items.ShouldBeEmpty();
        r.SuppressedByBaseline.ShouldBe(3);
    }

    [Fact]
    public void A_fixed_backlog_finding_leaves_both_sets_with_no_residue()
    {
        CommitAndAccept();
        _v.Write("Other/Elsewhere.md", "Unbracketed now: Also Missing\n");

        var r = Service().Lint(new LintQuery());
        r.Items.ShouldBeEmpty();
        r.SuppressedByBaseline.ShouldBe(2);
    }

    [Fact]
    public void A_link_made_ambiguous_by_ANOTHER_note_is_new()
    {
        // The baseline is re-linted, not a stored list, so a change in one
        // note that alters what a link in another means is visible — the
        // linking note itself is byte-identical to the baseline.
        // (Neither Target sits in Notes/, so the nearest-folder tie-break
        // cannot settle it.)
        CommitAndAccept();
        _v.Write("Archive/Target.md", "# A second Target\n");

        var r = Service().Lint(new LintQuery());
        Subjects(r).ShouldBe(
        [
            "ambiguous_link:Notes/Hub.md:Target#Missing Heading",
            "ambiguous_link:Notes/Hub.md:Target",
        ]);
        // The anchor finding is gone rather than suppressed: which file the
        // anchor belongs to is exactly what is now undecided.
        r.SuppressedByBaseline.ShouldBe(2);
    }

    [Fact]
    public void A_second_identical_finding_in_the_same_note_is_new()
    {
        // Counted, not a set: a set would hide the new occurrence behind the
        // old one. Which of two identical links is the new one is not
        // knowable, so both are reported.
        CommitAndAccept();
        _v.Write("Other/Elsewhere.md", "[[Also Missing]]\nAgain: [[Also Missing]]\n");

        var r = Service().Lint(new LintQuery());
        Subjects(r).ShouldBe(
        [
            "unresolved_link:Other/Elsewhere.md:Also Missing",
            "unresolved_link:Other/Elsewhere.md:Also Missing",
        ]);
        r.SuppressedByBaseline.ShouldBe(2);
    }

    [Fact]
    public void All_reports_the_backlog_and_names_no_baseline()
    {
        CommitAndAccept();
        _v.Write("Notes/New.md", "[[Brand New Missing]]\n");

        var r = Service().Lint(new LintQuery { All = true });
        r.Items.Count.ShouldBe(4);
        r.BaselineCommit.ShouldBeNull();
        r.SuppressedByBaseline.ShouldBe(0);
    }

    [Fact]
    public void A_run_never_advances_the_baseline()
    {
        // Report-once would forgive everything the first time it is not acted
        // on. The same new finding must nag on every run until it is fixed or
        // an operator accepts it.
        CommitAndAccept();
        var before = File.ReadAllBytes(_baselinePath);
        _v.Write("Notes/New.md", "[[Brand New Missing]]\n");
        _git.Commit(TimeSpan.FromSeconds(10)); // even once it is in history

        var lint = Service();
        lint.Lint(new LintQuery()).Items.Count.ShouldBe(1);
        lint.Lint(new LintQuery()).Items.Count.ShouldBe(1);
        File.ReadAllBytes(_baselinePath).ShouldBe(before);
    }

    [Fact]
    public void Accepting_again_absorbs_what_was_new_and_names_what_it_replaced()
    {
        var first = CommitAndAccept();
        _v.Write("Notes/New.md", "[[Brand New Missing]]\n");

        var second = CommitAndAccept();
        second.PreviousCommit.ShouldBe(first.Commit);
        second.Commit.ShouldNotBe(first.Commit);
        second.AcceptedFindings.ShouldBe(4);
        Service().Lint(new LintQuery()).Items.ShouldBeEmpty();
    }

    [Fact]
    public void Accept_names_the_visible_files_no_baseline_can_hold()
    {
        // The live lint does not honor .gitignore (anything that can write it
        // could hide notes from lint), so an ignored note's findings can never
        // be accepted. The accept must say so rather than leave it a mystery.
        File.AppendAllText(_v.Absolute(".gitignore"), "Private/\n");
        _v.Write("Private/Ignored.md", "[[Nowhere]]\n");

        var accepted = CommitAndAccept();
        accepted.NotInCommit.ShouldBe(["Private/Ignored.md"]);
        Subjects(Service().Lint(new LintQuery()))
            .ShouldBe([$"{LintChecks.UnresolvedLink}:Private/Ignored.md:Nowhere"]);
    }

    [Fact]
    public void A_symlinked_baseline_record_is_refused_never_followed()
    {
        var real = CommitAndAccept();
        var link = Path.Combine(_v.Outside.Path, "state", "linked-baseline.json");
        File.CreateSymbolicLink(link, _baselinePath);

        Should.Throw<KnapperException>(() => new LintBaselineStore(link).Read())
            .Message.ShouldContain("unusable");
        new LintBaselineStore(_baselinePath).Read()!.Commit.ShouldBe(real.Commit);
    }

    [Fact]
    public void An_unusable_baseline_fails_loud_and_all_still_answers()
    {
        // Neither silent option is acceptable: "absent" floods the caller with
        // the backlog for the wrong reason, and "nothing new" is the silence
        // this feature exists to end.
        Directory.CreateDirectory(Path.GetDirectoryName(_baselinePath)!);
        File.WriteAllText(_baselinePath, "{ not json");

        var e = Should.Throw<KnapperException>(() => Service().Lint(new LintQuery()));
        e.Code.ShouldBe(VaultErrorCode.IoError);
        e.Message.ShouldContain("knapper lint --accept");
        Service().Lint(new LintQuery { All = true }).Items.Count.ShouldBe(3);
    }

    [Fact]
    public void A_baseline_naming_a_commit_the_repository_lacks_fails_loud()
    {
        CommitAndAccept();
        var missing = new string('a', 40);
        File.WriteAllText(_baselinePath,
            $$"""{ "commit": "{{missing}}", "acceptedAt": "2026-09-27T00:00:00+00:00" }""");

        var e = Should.Throw<KnapperException>(() => Service().Lint(new LintQuery()));
        e.Code.ShouldBe(VaultErrorCode.IoError);
        e.Message.ShouldContain(missing);
    }

    [Theory]
    [InlineData("""{ "commit": "HEAD", "acceptedAt": "2026-09-27T00:00:00+00:00" }""")]
    [InlineData("""{ "commit": "--output=/tmp/x", "acceptedAt": "2026-09-27T00:00:00+00:00" }""")]
    [InlineData("""{ "acceptedAt": "2026-09-27T00:00:00+00:00" }""")]
    public void A_baseline_record_names_a_full_commit_or_nothing_reaches_git(string record)
    {
        // The only revision ever read from disk and handed to git is a full
        // hex object name: never a ref that can move, never an option.
        Directory.CreateDirectory(Path.GetDirectoryName(_baselinePath)!);
        File.WriteAllText(_baselinePath, record);
        Should.Throw<KnapperException>(() => Service().Lint(new LintQuery()))
            .Code.ShouldBe(VaultErrorCode.IoError);
    }

    [Fact]
    public void A_cursor_does_not_survive_an_accept()
    {
        // The accepted baseline is part of what the query means: resuming a
        // page across an accept would omit or repeat findings silently.
        _v.Write("Notes/More.md", "[[Missing A]] [[Missing B]] [[Missing C]]\n");
        var lint = Service();
        var page = lint.Lint(new LintQuery { MaxResults = 2 });
        page.Truncated.ShouldBeTrue();

        CommitAndAccept(lint);
        Should.Throw<KnapperException>(() => lint.Lint(new LintQuery { MaxResults = 2, Cursor = page.NextCursor }))
            .Code.ShouldBe(VaultErrorCode.InvalidCursor);
    }

    [Fact]
    public void Pagination_pages_the_new_findings_only()
    {
        CommitAndAccept();
        _v.Write("Notes/New.md", "[[N1]] [[N2]] [[N3]]\n");
        var lint = Service();

        var first = lint.Lint(new LintQuery { MaxResults = 2 });
        first.Items.Count.ShouldBe(2);
        first.TotalMatches.ShouldBe(3);
        var second = lint.Lint(new LintQuery { MaxResults = 2, Cursor = first.NextCursor });
        second.Truncated.ShouldBeFalse();
        Subjects(second).ShouldBe(["unresolved_link:Notes/New.md:N3"]);
    }

    [Fact]
    public void The_baseline_tree_is_read_under_the_working_trees_visibility_rules()
    {
        // A committed dot-folder note and a non-UTF-8 note are invisible /
        // unexamined in the live walk, so they must be the same in the
        // baseline — a baseline that saw a different vault would diff two
        // different vaults.
        _v.Write("Visible/.hidden/Secret.md", "[[Hidden Missing]]\n");
        File.WriteAllBytes(Path.Combine(_v.VaultDir.Path, "Notes/legacy.md"), [.. "caf"u8, 0xE9, .. " [[x]]\n"u8]);

        var accepted = CommitAndAccept();
        accepted.AcceptedFindings.ShouldBe(3);
        accepted.UnexaminedFiles.ShouldBe(["Notes/legacy.md"]);
        Service().Lint(new LintQuery()).Items.ShouldBeEmpty();
    }

    [Fact]
    public void Accept_refuses_without_a_configured_path_or_a_commit()
    {
        Should.Throw<KnapperException>(() => Service(baselinePath: "").Accept(DateTimeOffset.UtcNow))
            .Code.ShouldBe(VaultErrorCode.InvalidArgument);
        Should.Throw<KnapperException>(() => Service().Accept(DateTimeOffset.UtcNow))
            .Code.ShouldBe(VaultErrorCode.NotFound); // no commit yet
        File.Exists(_baselinePath).ShouldBeFalse();
    }

    [Fact]
    public void The_baseline_path_must_be_absolute_and_outside_the_vault()
    {
        LintBaselineStore.ValidatePath("", _v.Resolver.Root).ShouldBeNull();
        LintBaselineStore.ValidatePath(_baselinePath, _v.Resolver.Root).ShouldBeNull();
        LintBaselineStore.ValidatePath("relative/baseline.json", _v.Resolver.Root).ShouldNotBeNull();
        LintBaselineStore.ValidatePath(Path.Combine(_v.Resolver.Root, "baseline.json"), _v.Resolver.Root)
            .ShouldNotBeNull().ShouldContain("INSIDE");
    }

    [Fact]
    public void A_git_that_stalls_mid_tree_is_killed_at_the_budget_and_nothing_is_cached()
    {
        // Lint answers on the request path: a cat-file that never answers must
        // cost one typed QueryTimeout, not a hung call — and must not leave a
        // half-read baseline behind to be served as an answer.
        CommitAndAccept();
        var lint = Service(options: new VaultOptions
        {
            RootPath = _v.Resolver.Root,
            LockDirectory = _v.Options.LockDirectory,
            AuditLogPath = _v.Options.AuditLogPath,
            QueryTimeoutMs = 1_500,
        });
        var fake = Path.Combine(_v.Outside.Path, "stalling-git");
        File.WriteAllText(fake,
            "#!/bin/sh\nfor a in \"$@\"; do [ \"$a\" = \"--batch\" ] && exec sleep 60; done\nexec git \"$@\"\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        lint.Git.GitExecutable = fake;

        var started = Environment.TickCount64;
        Should.Throw<KnapperException>(() => lint.Lint(new LintQuery()))
            .Code.ShouldBe(VaultErrorCode.QueryTimeout);
        (Environment.TickCount64 - started).ShouldBeLessThan(10_000);

        lint.Git.GitExecutable = "git";
        lint.Lint(new LintQuery()).SuppressedByBaseline.ShouldBe(3);
    }
}
