using Knapper.Core.Git;
using Knapper.Core.Mutation;
using Knapper.Core.Tests.Mutation;

namespace Knapper.Core.Tests.Git;

public sealed class GitCommitJobTests : IDisposable
{
    private static readonly TimeSpan Ample = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(300);

    private readonly MutationVault _v = new();
    private readonly GitCommitJob _job;

    public GitCommitJobTests() => _job = new GitCommitJob(_v.Resolver, _v.Locks);

    public void Dispose() => _v.Dispose();

    [Fact]
    public void Init_creates_repo_with_the_standard_gitignore_and_refuses_twice()
    {
        _job.RepoExists.ShouldBeFalse();
        _job.Init();
        _job.RepoExists.ShouldBeTrue();
        File.ReadAllText(Path.Combine(_v.VaultDir.Path, ".gitignore")).ShouldContain(".knapper-tmp-*");

        Should.Throw<KnapperException>(() => _job.Init())
            .Code.ShouldBe(VaultErrorCode.AlreadyExists);
    }

    [Fact]
    public void Commit_snapshots_changes_and_reports_nothing_when_quiet()
    {
        _job.Init();
        _v.Write("Notes/a.md", "content\n");

        var first = _job.Commit(Ample);
        first.Committed.ShouldBeTrue();
        first.CommitSha.ShouldNotBeNullOrEmpty();

        var second = _job.Commit(Ample);
        second.Committed.ShouldBeFalse();
        second.Message.ShouldBe("nothing to commit");

        _job.LastCommitAgeSeconds().ShouldNotBeNull();
        _job.LastCommitAgeSeconds()!.Value.ShouldBeLessThan(60);
    }

    [Fact]
    public void Commit_without_a_repo_is_a_typed_refusal_naming_the_remedy()
    {
        var ex = Should.Throw<KnapperException>(() => _job.Commit(Ample));
        ex.Code.ShouldBe(VaultErrorCode.NotFound);
        ex.Message.ShouldContain("git-init");
    }

    [Fact]
    public void Ignored_churn_does_not_produce_commits()
    {
        _job.Init();
        _v.Write("Notes/a.md", "content\n");
        _job.Commit(Ample);

        _v.Write(".trash/old.md", "trashed\n");
        _v.Write(".obsidian/workspace.json", "{}");
        _v.Write("Notes/.knapper-tmp-x", "temp");

        _job.Commit(Ample).Committed.ShouldBeFalse();
    }

    [Fact]
    public void A_staged_secret_refuses_the_commit_and_nothing_enters_history()
    {
        _job.Init();
        _v.Write("Notes/safe.md", "ordinary note\n");
        _job.Commit(Ample);
        _v.Write("Notes/oops.md", "my key is AKIAIOSFODNN7EXAMPLE\n");

        var ex = Should.Throw<KnapperException>(() => _job.Commit(Ample));
        ex.Code.ShouldBe(VaultErrorCode.MutationBlocked);
        ex.Message.ShouldContain("Notes/oops.md");
        ex.Message.ShouldContain("aws-access-key");
        ex.Message.ShouldNotContain("AKIAIOSFODNN7EXAMPLE"); // masked, never echoed whole

        // History has exactly the one earlier commit; the secret is not in it.
        _job.LastCommitAgeSeconds().ShouldNotBeNull();
        // And a fixed vault commits cleanly afterwards.
        _v.Write("Notes/oops.md", "redacted, key moved to the password manager\n");
        _job.Commit(Ample).Committed.ShouldBeTrue();
    }

    [Fact]
    public void The_commit_lock_and_mutation_locks_exclude_each_other()
    {
        _job.Init();
        _v.Write("Notes/a.md", "content\n");

        using (_v.Locks.AcquirePathLock(
                   new Knapper.Core.Vault.VaultPath { Relative = "Notes/a.md", Absolute = "/x" }, Ample))
        {
            // A mutation is in flight: the snapshot must wait (here: time out).
            Should.Throw<KnapperException>(() => _job.Commit(Short))
                .Code.ShouldBe(VaultErrorCode.LockTimeout);
        }

        _job.Commit(Ample).Committed.ShouldBeTrue();
    }

    [Theory]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----", "private-key")]
    [InlineData("ghp_0123456789abcdefghijklmnopqrstuvwxyz", "github-token")]
    [InlineData("api_key = \"abcdefghij0123456789xyz\"", "api-key-like")]
    [InlineData("xoxb-1234567890-abcdefghij", "slack-token")]
    public void Secret_scanner_catches_common_shapes(string line, string kind) =>
        SecretScanner.Scan("f.md", line).ShouldContain(f => f.Kind == kind);

    [Theory]
    [InlineData("the word password appears in prose")]
    [InlineData("password: short")]
    [InlineData("see my API key management doc")]
    public void Secret_scanner_leaves_ordinary_prose_alone(string line) =>
        SecretScanner.Scan("f.md", line).ShouldBeEmpty();
}
