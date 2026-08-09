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
    public void Success_stamp_is_touched_on_commit_and_on_quiet_runs_but_not_on_refusal()
    {
        // The stamp — not last-commit age — is the monitor's freshness
        // signal: the job deliberately skips empty commits, so HEAD age
        // can't distinguish a quiet vault from a dead timer.
        var stamp = Path.Combine(_v.Outside.Path, "commit-stamp");
        _job.Init();
        _v.Write("Notes/a.md", "content\n");

        _job.Commit(Ample, stamp).Committed.ShouldBeTrue();
        File.Exists(stamp).ShouldBeTrue();

        File.Delete(stamp);
        _job.Commit(Ample, stamp).Committed.ShouldBeFalse(); // quiet run...
        File.Exists(stamp).ShouldBeTrue();                   // ...still stamps

        File.Delete(stamp);
        _v.Write("Notes/oops.md", "my key is AKIAIOSFODNN7EXAMPLE\n");
        Should.Throw<KnapperException>(() => _job.Commit(Ample, stamp))
            .Code.ShouldBe(VaultErrorCode.MutationBlocked);
        File.Exists(stamp).ShouldBeFalse("a refused run must NOT stamp — the monitor exists to notice it");
    }

    [Theory]
    [InlineData("Notes/evil\nname.md")]      // newline: breaks '\n'-split name lists
    [InlineData("(icase)note.md")]           // pathspec-magic shape: breaks `git show :path`
    [InlineData(":odd:name.md")]             // rev-syntax shape
    public void Unusual_staged_filenames_cannot_slip_a_secret_past_the_scan(string name)
    {
        _job.Init();
        _v.Write(name, "my key is AKIAIOSFODNN7EXAMPLE\n");

        // The blob-SHA scan route never interprets the filename: the secret
        // must be found and the commit refused, exactly as for a plain name.
        Should.Throw<KnapperException>(() => _job.Commit(Ample))
            .Code.ShouldBe(VaultErrorCode.MutationBlocked);
        _job.LastCommitAgeSeconds().ShouldBeNull("nothing may have entered history");
    }

    [Fact]
    public void Attachment_class_blobs_are_size_skipped_while_note_secrets_still_refuse()
    {
        _job.Init();
        // A synced attachment above the scan cap — even one containing a
        // credential-shaped string (documented tripwire limitation: the
        // scanner covers text notes, not blobs) — must not block or slow
        // the snapshot.
        var big = new byte[GitCommitJob.MaxScanBlobBytes + 1024];
        var secret = System.Text.Encoding.UTF8.GetBytes("AKIAIOSFODNN7EXAMPLE");
        secret.CopyTo(big, 4096);
        File.WriteAllBytes(Path.Combine(_v.VaultDir.Path, "attachment.pdf"), big);
        _v.Write("Notes/a.md", "plain note\n");

        _job.Commit(Ample).Committed.ShouldBeTrue();

        // The same secret in a NOTE still refuses — the cap didn't blunt
        // the tripwire where it matters.
        _v.Write("Notes/oops.md", "my key is AKIAIOSFODNN7EXAMPLE\n");
        Should.Throw<KnapperException>(() => _job.Commit(Ample))
            .Code.ShouldBe(VaultErrorCode.MutationBlocked);
    }

    [Fact]
    public void A_staged_deletion_still_commits_without_tripping_the_scan()
    {
        _job.Init();
        _v.Write("Notes/a.md", "plain content\n");
        _job.Commit(Ample).Committed.ShouldBeTrue();

        File.Delete(Path.Combine(_v.VaultDir.Path, "Notes/a.md"));
        var outcome = _job.Commit(Ample);
        outcome.Committed.ShouldBeTrue("a deletion has no new content to scan and must commit");
    }

    [Fact]
    public void An_unreadable_staged_blob_refuses_the_commit_instead_of_skipping_the_scan()
    {
        // A scan that cannot run must never pass as a scan that found
        // nothing. Make the staged blob's loose object unreadable so
        // cat-file fails (deleting it wouldn't work: the add -A inside
        // Commit would simply re-create the object from the working tree,
        // while an EXISTING object is never rewritten).
        _job.Init();
        const string content = "content whose object will be unreadable\n";
        _v.Write("Notes/a.md", content);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
        };
        foreach (var a in new[] { "-C", _v.VaultDir.Path, "hash-object", "--stdin" })
            psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.StandardInput.Write(content);
        p.StandardInput.Close();
        var sha = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit(10_000).ShouldBeTrue();

        // First commit attempt writes the object (and would succeed) — but
        // we only let it run `add` indirectly by staging here ourselves:
        using var add = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git", ArgumentList = { "-C", _v.VaultDir.Path, "add", "-A" },
        })!;
        add.WaitForExit(10_000).ShouldBeTrue();

        var objectPath = Path.Combine(_v.VaultDir.Path, ".git", "objects", sha[..2], sha[2..]);
        File.Exists(objectPath).ShouldBeTrue("expected a loose object for the staged blob");
        File.SetUnixFileMode(objectPath, UnixFileMode.None);
        try
        {
            Should.Throw<KnapperException>(() => _job.Commit(Ample))
                .Code.ShouldBe(VaultErrorCode.IoError);
            _job.LastCommitAgeSeconds().ShouldBeNull("nothing may have been committed unscanned");
        }
        finally
        {
            File.SetUnixFileMode(objectPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void An_in_vault_stamp_path_is_refused_before_any_stamp_is_written()
    {
        _job.Init();
        _v.Write("Notes/a.md", "content\n");

        // Inside the vault: would sync, and each quiet run would dirty the
        // tree for the next — a self-sustaining commit loop.
        var inVault = Path.Combine(_v.VaultDir.Path, "stamp");
        Should.Throw<KnapperException>(() => _job.Commit(Ample, inVault))
            .Code.ShouldBe(VaultErrorCode.InvalidArgument);
        File.Exists(inVault).ShouldBeFalse();

        // Equality and a symlinked ancestor are the same violation.
        Should.Throw<KnapperException>(() => _job.Commit(Ample, _v.VaultDir.Path))
            .Code.ShouldBe(VaultErrorCode.InvalidArgument);
        var link = Path.Combine(_v.Outside.Path, "sneaky");
        File.CreateSymbolicLink(link, _v.VaultDir.Path);
        Should.Throw<KnapperException>(() => _job.Commit(Ample, Path.Combine(link, "stamp")))
            .Code.ShouldBe(VaultErrorCode.InvalidArgument);
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
