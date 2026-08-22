using System.Diagnostics;
using System.Text;
using Knapper.Core.Locking;
using Knapper.Core.Vault;

namespace Knapper.Core.Git;

/// <summary>
/// The vault's ONLY git committer (brief §10): local-only repo inside the
/// vault, snapshots taken under the vault-wide commit lock so a prepared-
/// but-unverified mutation can never be captured mid-flight. NO remote —
/// ever, until the credential sweep closes — and the pre-commit secret scan
/// keeps new credentials from entering history in the meantime.
/// </summary>
public sealed class GitCommitJob(VaultPathResolver resolver, VaultLockManager locks)
{
    /// <summary>Brief §10's minimum ignore set. Written once by Init; deliberately overwritable by hand.</summary>
    public const string GitIgnore =
        """
        .obsidian/workspace.json
        .obsidian/workspace-mobile.json
        .DS_Store
        .trash/
        .knapper-tmp-*
        *.tmp
        """;

    public sealed record CommitOutcome(bool Committed, string? CommitSha, string Message);

    /// <summary>
    /// Blobs above this are not text-scanned. The scanner is a tripwire for
    /// credential-shaped TEXT in notes; anything this large is
    /// attachment-class (Sync deliberately delivers image/audio/video/pdf —
    /// brief §5), and cat-file'ing it would materialize every synced photo
    /// into a string on every commit run. A DOCUMENTED limitation like the
    /// scanner's line-based matching — not a silent skip: the size check
    /// itself failing still refuses the commit.
    /// </summary>
    public const long MaxScanBlobBytes = 4_000_000;

    /// <summary>
    /// Wall-clock bound on any one git invocation. Every git call in this
    /// class runs while the vault-wide commit lock is held EXCLUSIVELY, and
    /// every mutation needs that lock in shared mode — so a git that never
    /// returns does not merely fail a commit, it blocks all vault writes with
    /// no caller in a position to time it out. Sized for the worst honest
    /// case (an `add -A` restaging a whole large vault on a cold cache), not
    /// for the typical one, because the cost of expiring early is a skipped
    /// commit cycle and the cost of never expiring is a wedged vault.
    /// </summary>
    public const int GitTimeoutMs = 120_000;

    /// <summary>Test seams: a stand-in git, and a bound short enough to assert.</summary>
    internal string GitExecutable = "git";
    internal int TimeoutMs = GitTimeoutMs;

    public bool RepoExists => Directory.Exists(Path.Combine(resolver.Root, ".git"));

    /// <summary>
    /// Brief §10: the vault repo is LOCAL-ONLY until the credential sweep
    /// closes. Enforcement was previously by absence — nothing noticed a
    /// human adding a remote after init. `knapper doctor` fails loud on it.
    /// </summary>
    public bool HasRemote() => RepoExists && Run("remote").Trim().Length > 0;

    /// <summary>git init + .gitignore + identity. A deliberate act: once .git exists, PBS backups are the only protection for history.</summary>
    public void Init()
    {
        if (RepoExists)
            throw new KnapperException(VaultErrorCode.AlreadyExists, "the vault is already a git repository");
        Run("init");
        var gitignore = Path.Combine(resolver.Root, ".gitignore");
        if (!File.Exists(gitignore))
            AtomicFile.CreateNew(gitignore, Encoding.UTF8.GetBytes(GitIgnore + "\n"));
        Run("config", "user.name", "Knapper");
        Run("config", "user.email", "knapper@localhost");
    }

    /// <summary>
    /// Snapshot the vault: commit lock → add -A → staged secret scan →
    /// commit. Returns without committing when nothing changed. Throws
    /// <see cref="VaultErrorCode.MutationBlocked"/> with the findings when
    /// the secret scan trips — the vault must not accept new secrets.
    /// When <paramref name="successStampPath"/> is set, EVERY successful run
    /// (including "nothing to commit") durably touches that stamp: it is the
    /// external monitor's freshness signal, because last-commit age cannot
    /// distinguish a quiet vault from a dead timer — this job deliberately
    /// creates no commit when nothing changed. A failed run (refused commit,
    /// I/O error) writes no stamp, so the stamp goes stale and the monitor
    /// fires.
    /// </summary>
    public CommitOutcome Commit(TimeSpan lockTimeout, string? successStampPath = null)
    {
        if (!RepoExists)
        {
            throw new KnapperException(VaultErrorCode.NotFound,
                "the vault is not a git repository — run `knapper git-init` first (a deliberate act; see brief §10)");
        }

        using (locks.AcquireCommitLock(lockTimeout))
        {
            Run("add", "-A");

            // --raw -z: NUL-delimited entries carrying each change's NEW BLOB
            // SHA and status letter. Names split on '\n' (or any pathspec use
            // with agent/Sync-controlled filenames) would let a hostile or
            // merely unusual filename slip a file past the scan — the blob
            // SHA route never interprets a filename at all.
            var staged = Run("diff", "--cached", "--raw", "-z", "--no-renames");
            if (staged.Length == 0)
                return Stamped(new CommitOutcome(false, null, "nothing to commit"), successStampPath);

            var findings = ScanStaged(staged);
            if (findings.Count > 0)
            {
                // Unstage so a later hand inspection sees the working tree
                // exactly as Sync left it.
                Run("reset");
                var described = string.Join("; ", findings.Select(f => $"{f.File}:{f.Line} {f.Kind} ({f.Masked})"));
                throw new KnapperException(VaultErrorCode.MutationBlocked,
                    $"commit refused: credential-shaped content is staged — {described}. " +
                    "Remove the secret from the note (secrets never belong in the vault), then commit again.");
            }

            var message = $"knapper snapshot {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z";
            Run("commit", "-m", message);
            var sha = Run("rev-parse", "HEAD").Trim();
            return Stamped(new CommitOutcome(true, sha, message), successStampPath);
        }
    }

    private CommitOutcome Stamped(CommitOutcome outcome, string? stampPath)
    {
        if (string.IsNullOrWhiteSpace(stampPath))
            return outcome;
        // Same containment rule as the lock dir and audit log: a stamp
        // INSIDE the vault would sync — and worse, every "nothing to
        // commit" run would dirty the tree and feed the next run a change,
        // a self-sustaining commit loop.
        if (PathContainment.IsInsideOrEqual(stampPath, resolver.Root))
        {
            throw new KnapperException(VaultErrorCode.InvalidArgument,
                $"Vault:CommitStampPath ('{stampPath}') is the vault or INSIDE it — " +
                "operational files must never sync (and an in-vault stamp would trigger the next commit).");
        }
        // Durable (fsynced): a stamp that evaporates on power loss would
        // false-alarm the monitor after every crash-and-recover.
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(stampPath))!);
        using var stream = new FileStream(stampPath, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.Read,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        stream.Write(Encoding.UTF8.GetBytes(
            $"{DateTimeOffset.UtcNow:O} {(outcome.Committed ? outcome.CommitSha : "nothing-to-commit")}\n"));
        stream.Flush(flushToDisk: true);
        return outcome;
    }

    /// <summary>Age of HEAD in seconds, or null when there is no commit yet — for the freshness monitor.</summary>
    public double? LastCommitAgeSeconds()
    {
        if (!RepoExists)
            return null;
        try
        {
            var epoch = Run("log", "-1", "--format=%ct").Trim();
            return long.TryParse(epoch, out var seconds)
                ? (DateTime.UtcNow - DateTime.UnixEpoch.AddSeconds(seconds)).TotalSeconds
                : null;
        }
        catch (KnapperException)
        {
            return null; // empty repo
        }
    }

    /// <summary>
    /// Scan every staged blob, FAIL CLOSED. Input is `diff --cached --raw -z`
    /// output: ":oldmode newmode oldsha newsha status\0path\0" per entry.
    /// Deletions (status D) are the ONLY skip — nothing of theirs enters
    /// history. Everything else is fetched by its blob SHA via cat-file (the
    /// STAGED bytes, and no filename is ever interpreted as a pathspec or
    /// rev), and ANY fetch failure refuses the whole commit: a scan that
    /// cannot run must never be mistaken for a scan that found nothing.
    /// </summary>
    private List<SecretScanner.Finding> ScanStaged(string rawZ)
    {
        var findings = new List<SecretScanner.Finding>();
        var tokens = rawZ.Split('\0');
        for (var i = 0; i + 1 < tokens.Length; i += 2)
        {
            var meta = tokens[i];
            var path = tokens[i + 1];
            if (meta.Length == 0)
                break; // trailing NUL
            var fields = meta.TrimStart(':').Split(' ');
            if (fields.Length < 5)
            {
                throw new KnapperException(VaultErrorCode.IoError,
                    "unparseable `git diff --cached --raw -z` entry — refusing to commit unscanned content");
            }
            var newBlobSha = fields[3];
            var status = fields[4];
            if (status.StartsWith('D'))
                continue; // deletion: no new content can enter history

            string content;
            try
            {
                // Size first (cat-file -s is metadata-only): attachment-class
                // blobs are skipped per MaxScanBlobBytes, never materialized.
                var size = long.Parse(Run("cat-file", "-s", newBlobSha).Trim());
                if (size > MaxScanBlobBytes)
                    continue;
                content = Run("cat-file", "blob", newBlobSha);
            }
            catch (Exception e) when (e is KnapperException or FormatException or OverflowException)
            {
                throw new KnapperException(VaultErrorCode.IoError,
                    $"cannot read the staged blob for '{path}' — refusing to commit unscanned content (fail closed)", e);
            }
            findings.AddRange(SecretScanner.Scan(path, content));
        }
        return findings;
    }

    /// <summary>git with structured args against the vault; never a shell.</summary>
    private string Run(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = GitExecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(resolver.Root);
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new KnapperException(VaultErrorCode.IoError, "failed to start git");
        // Both pipes drained CONCURRENTLY, and the wait is BOUNDED. This runs
        // under the vault-wide commit lock, which every mutation needs in
        // shared mode, so anything that blocks here blocks all writes until
        // the process restarts — there is no caller to time it out.
        //
        // Draining one pipe to EOF before starting the other deadlocks the
        // moment git emits more than a pipe buffer on the stream nobody is
        // reading: the child blocks writing stderr, the parent blocks reading
        // stdout, and neither ever moves. The bound covers the rest — a git
        // that hangs without filling a pipe at all (a stalled filesystem, an
        // index.lock it decides to wait on) wedges the lock just as hard, and
        // a commit is a background job: failing it loudly costs one cycle,
        // and the next tick picks the work up.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException
                or System.ComponentModel.Win32Exception)
            {
                // Already gone between the timeout and the kill, or unkillable.
                // Either way the throw below is the answer.
            }
            throw new KnapperException(VaultErrorCode.IoError,
                $"git {args[0]} did not exit within {TimeoutMs} ms and was killed — " +
                "the commit is abandoned and will be retried on the next tick");
        }
        // WaitForExit(int) does not itself await the redirected streams, and
        // this wait is bounded for the same reason the one above is: a
        // grandchild inheriting the pipe holds it open past git's own exit,
        // and an unbounded wait here would hand back the wedge the timeout
        // just removed.
        if (!Task.WaitAll([stdoutTask, stderrTask], TimeoutMs))
        {
            throw new KnapperException(VaultErrorCode.IoError,
                $"git {args[0]} exited but its output pipes stayed open past {TimeoutMs} ms — " +
                "the commit is abandoned and will be retried on the next tick");
        }
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new KnapperException(VaultErrorCode.IoError,
                $"git {args[0]} failed ({process.ExitCode}): {stderr.Trim()}");
        }
        return stdout;
    }
}
