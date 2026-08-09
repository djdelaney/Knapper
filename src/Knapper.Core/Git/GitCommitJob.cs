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

    public bool RepoExists => Directory.Exists(Path.Combine(resolver.Root, ".git"));

    /// <summary>git init + .gitignore + identity. A deliberate act: once .git exists, PBS backups are the only protection for history.</summary>
    public void Init()
    {
        if (RepoExists)
            throw new KnapperException(VaultErrorCode.AlreadyExists, "the vault is already a git repository");
        Run("init");
        var gitignore = Path.Combine(resolver.Root, ".gitignore");
        if (!File.Exists(gitignore))
            File.WriteAllText(gitignore, GitIgnore + "\n");
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
                content = Run("cat-file", "blob", newBlobSha);
            }
            catch (KnapperException e)
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
            FileName = "git",
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
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new KnapperException(VaultErrorCode.IoError,
                $"git {args[0]} failed ({process.ExitCode}): {stderr.Trim()}");
        }
        return stdout;
    }
}
