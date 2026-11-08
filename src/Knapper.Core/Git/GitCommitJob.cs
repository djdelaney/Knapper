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

    /// <summary>The attachment cap, now owned by <see cref="SecretScanPolicy"/> (shared with the upload pre-scan).</summary>
    public const long MaxScanBlobBytes = SecretScanPolicy.MaxScanBytes;

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
            RequireNoFilterDrivers();
            Run("add", "-A");

            // --raw -z: NUL-delimited entries carrying each change's NEW BLOB
            // SHA and status letter. Names split on '\n' (or any pathspec use
            // with agent/Sync-controlled filenames) would let a hostile or
            // merely unusual filename slip a file past the scan — the blob
            // SHA route never interprets a filename at all.
            var staged = Run("diff", "--cached", "--raw", "-z", "--no-renames");
            if (staged.Length == 0)
                return Stamped(new CommitOutcome(false, null, "nothing to commit"), successStampPath);

            RequireNoContentConversion(staged);
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

    /// <summary>
    /// A `filter.&lt;name&gt;.clean` (or `.process`) driver runs a program on
    /// every matching file during `add`, under a name chosen by whoever wrote
    /// the config, so no fixed `-c` override can neutralize it. Knapper never
    /// configures one, so any present — including one pulled in through an
    /// include — is refused as tampering rather than run. Reading config runs
    /// nothing.
    /// </summary>
    private void RequireNoFilterDrivers()
    {
        var keys = Run("config", "--list", "--name-only", "-z")
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var drivers = keys.Where(k => k.StartsWith("filter.", StringComparison.OrdinalIgnoreCase)).ToList();
        if (drivers.Count > 0)
        {
            throw new KnapperException(VaultErrorCode.MutationBlocked,
                $"commit refused: the vault's git config defines a filter driver ({string.Join(", ", drivers)}), " +
                "which would run a program during `git add`. Knapper never sets one — treat it as tampering, " +
                "inspect .git/config, and remove it before committing again.");
        }
    }

    /// <summary>
    /// The attributes `git add` uses to RE-ENCODE content on its way into the
    /// index. The secret scan reads the STAGED blob, and the upload commit's
    /// pre-landing scan reads the payload's own bytes; the two agree only if
    /// staging leaves the bytes alone. `working-tree-encoding` does not: a
    /// credential stored as UTF-16 without a mark is invisible to the
    /// pre-scan and appears in UTF-8 in the staged blob, so an upload passes
    /// and then refuses every vault commit — the exact failure the shared
    /// policy exists to prevent (review 2026-11-08). Line-ending
    /// normalization and `ident` cannot CREATE a credential-shaped run, so
    /// they stay legal; `filter` is refused separately, before `add`.
    /// </summary>
    internal static readonly string[] ContentConvertingAttributes = ["working-tree-encoding"];

    /// <summary>
    /// Refuse the commit when any staged path has an effective
    /// content-converting attribute — from an in-tree `.gitattributes` (vault
    /// content Sync delivers) or `.git/info/attributes` (writable by the
    /// service account). Like a filter driver, Knapper never sets one, so it
    /// is refused rather than honoured; the index is reset so the tree is left
    /// as Sync left it. Reading attributes runs nothing.
    /// </summary>
    private void RequireNoContentConversion(string rawZ)
    {
        var paths = StagedEntries(rawZ).Where(e => !e.Status.StartsWith('D')).Select(e => e.Path).ToList();
        foreach (var chunk in paths.Chunk(200))
        {
            // -z: <path> NUL <attribute> NUL <value> NUL, per path and attribute.
            var answer = Run(["check-attr", "-z", .. ContentConvertingAttributes, "--", .. chunk])
                .Split('\0');
            var converting = new List<string>();
            for (var i = 0; i + 2 < answer.Length; i += 3)
            {
                if (answer[i + 2] is not ("unspecified" or "unset"))
                    converting.Add($"{answer[i]} ({answer[i + 1]}={answer[i + 2]})");
            }
            if (converting.Count > 0)
            {
                Run("reset");
                throw new KnapperException(VaultErrorCode.MutationBlocked,
                    $"commit refused: git attributes would re-encode staged content ({string.Join(", ", converting.Take(5))}" +
                    $"{(converting.Count > 5 ? ", …" : "")}), so the secret scan could not see what enters history. " +
                    "Knapper never sets one — inspect .gitattributes in the vault and .git/info/attributes, remove " +
                    "the attribute, and commit again.");
            }
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

    /// <summary>
    /// Scan every staged blob, FAIL CLOSED. Input is `diff --cached --raw -z`
    /// output: ":oldmode newmode oldsha newsha status\0path\0" per entry.
    /// Deletions (status D) are the ONLY skip — nothing of theirs enters
    /// history. Everything else is fetched by its blob SHA via cat-file (the
    /// STAGED bytes, and no filename is ever interpreted as a pathspec or
    /// rev), and ANY fetch failure refuses the whole commit: a scan that
    /// cannot run must never be mistaken for a scan that found nothing.
    /// </summary>
    /// <summary>
    /// The entries of `diff --cached --raw -z`: NUL-framed (meta, path)
    /// pairs. Unparseable input fails closed — never a partial list that
    /// would leave content unscanned.
    /// </summary>
    private static List<(string BlobSha, string Status, string Path)> StagedEntries(string rawZ)
    {
        var entries = new List<(string, string, string)>();
        var tokens = rawZ.Split('\0');
        for (var i = 0; i + 1 < tokens.Length; i += 2)
        {
            var meta = tokens[i];
            if (meta.Length == 0)
                break; // trailing NUL
            var fields = meta.TrimStart(':').Split(' ');
            if (fields.Length < 5)
            {
                throw new KnapperException(VaultErrorCode.IoError,
                    "unparseable `git diff --cached --raw -z` entry — refusing to commit unscanned content");
            }
            entries.Add((fields[3], fields[4], tokens[i + 1]));
        }
        return entries;
    }

    private List<SecretScanner.Finding> ScanStaged(string rawZ)
    {
        var findings = new List<SecretScanner.Finding>();
        foreach (var (newBlobSha, status, path) in StagedEntries(rawZ))
        {
            if (status.StartsWith('D'))
                continue; // deletion: no new content can enter history

            string content;
            try
            {
                // Size first (cat-file -s is metadata-only): attachment-class
                // blobs are skipped per MaxScanBlobBytes, never materialized.
                var size = long.Parse(Run("cat-file", "-s", newBlobSha).Trim());
                // Coverage and decoding are SecretScanPolicy's, never decided
                // here: the upload commit's pre-landing scan uses the same
                // two calls, and a disagreement admits a file that then
                // refuses every commit (transfer-grants §4.3).
                if (!SecretScanPolicy.ShouldScan(path, size))
                    continue;
                content = SecretScanPolicy.Decode(RunBytes("cat-file", "blob", newBlobSha));
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

    /// <summary>
    /// git against the vault, via the one neutralized, bounded runner
    /// (<see cref="GitProcess"/>). The bound matters most HERE: every call in
    /// this class runs under the vault-wide commit lock, which every mutation
    /// needs in shared mode, and a commit is a background job — failing it
    /// loudly costs one cycle, and the next tick picks the work up.
    /// </summary>
    private string Run(params string[] args) =>
        GitProcess.Run(GitExecutable, resolver.Root, TimeoutMs,
            "the commit is abandoned and will be retried on the next tick", args);

    private byte[] RunBytes(params string[] args) =>
        GitProcess.RunBytes(GitExecutable, resolver.Root, TimeoutMs,
            "the commit is abandoned and will be retried on the next tick", args);
}
