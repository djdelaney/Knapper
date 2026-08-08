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
    /// </summary>
    public CommitOutcome Commit(TimeSpan lockTimeout)
    {
        if (!RepoExists)
        {
            throw new KnapperException(VaultErrorCode.NotFound,
                "the vault is not a git repository — run `knapper git-init` first (a deliberate act; see brief §10)");
        }

        using (locks.AcquireCommitLock(lockTimeout))
        {
            Run("add", "-A");

            var staged = Run("diff", "--cached", "--name-only").Trim();
            if (staged.Length == 0)
                return new CommitOutcome(false, null, "nothing to commit");

            var findings = ScanStaged(staged.Split('\n'));
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
            return new CommitOutcome(true, sha, message);
        }
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

    private List<SecretScanner.Finding> ScanStaged(string[] stagedFiles)
    {
        var findings = new List<SecretScanner.Finding>();
        foreach (var file in stagedFiles.Where(f => f.Length > 0))
        {
            string content;
            try
            {
                // The STAGED blob, not the working tree — what would enter history.
                content = Run("show", $":{file}");
            }
            catch (KnapperException)
            {
                continue; // deleted in this change set
            }
            findings.AddRange(SecretScanner.Scan(file, content));
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
