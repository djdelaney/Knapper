using System.Diagnostics;
using System.Text;

namespace Knapper.Core.Git;

/// <summary>
/// Read-only access to COMMITTED vault trees, for lint's baseline
/// (docs/proposals/vault-lint.md §5). Reads objects only — rev-parse, ls-tree,
/// cat-file — so it takes no lock and never touches the index or the working
/// tree: git objects are immutable, and a commit landing concurrently adds
/// objects without changing any this reads.
/// </summary>
public sealed class GitTreeReader(string vaultRoot)
{
    /// <summary>A commit is a full hex object name: SHA-1 (40) or SHA-256 (64). Nothing else is ever passed to git as a revision from disk.</summary>
    public static bool IsCommitName(string value) =>
        value.Length is 40 or 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>One blob in a committed tree.</summary>
    public sealed record Entry(string Path, string Blob, long Size);

    /// <summary>Test seams: a stand-in git, and a bound short enough to assert.</summary>
    internal string GitExecutable = "git";
    internal int TimeoutMs = GitCommitJob.GitTimeoutMs;

    private const string Consequence = "the lint baseline could not be read";

    public bool RepoExists => Directory.Exists(Path.Combine(vaultRoot, ".git"));

    /// <summary>The full commit name HEAD points at. Throws NotFound on a repo with no commit yet.</summary>
    public string ResolveHead()
    {
        RequireRepo();
        try
        {
            return Run("rev-parse", "--verify", "HEAD^{commit}").Trim();
        }
        catch (KnapperException e) when (e.Code == VaultErrorCode.IoError)
        {
            throw new KnapperException(VaultErrorCode.NotFound,
                "the vault repository has no commit yet — run `knapper commit` first", e);
        }
    }

    /// <summary>True when <paramref name="commit"/> names a commit present in the vault repository.</summary>
    public bool CommitExists(string commit)
    {
        RequireRepo();
        if (!IsCommitName(commit))
            return false;
        // Exit 0: present. Non-zero: absent (or not a commit) — an answer,
        // not a failure. A timeout still throws: it answers nothing.
        return GitProcess.Execute(GitExecutable, vaultRoot, TimeoutMs, Consequence,
            "cat-file", "-e", commit + "^{commit}").ExitCode == 0;
    }

    /// <summary>
    /// Every regular-file blob in <paramref name="commit"/>'s tree, under the
    /// SAME visibility rule the working-tree lister applies: a dot-segment
    /// anywhere hides the path, and symlinks (mode 120000) and submodules are
    /// not files. A baseline that saw files the live walk cannot — or missed
    /// ones it can — would diff two different vaults.
    /// </summary>
    public List<Entry> ListFiles(string commit)
    {
        RequireRepo();
        if (!IsCommitName(commit))
            throw new KnapperException(VaultErrorCode.InvalidArgument, $"not a full commit name: '{commit}'");
        // -z: NUL-framed, so no filename is quoted, escaped or split. -l adds
        // the blob size, which lets an over-cap note be classified without
        // ever being read.
        var raw = Run("ls-tree", "-r", "-z", "-l", "--full-tree", commit);
        var entries = new List<Entry>();
        foreach (var record in raw.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = record.IndexOf('\t', StringComparison.Ordinal);
            if (tab < 0)
                throw Unparseable();
            var meta = record[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var path = record[(tab + 1)..];
            if (meta.Length != 4)
                throw Unparseable();
            var (mode, type, blob) = (meta[0], meta[1], meta[2]);
            if (type != "blob" || mode == "120000")
                continue;
            if (path.Split('/').Any(segment => segment.StartsWith('.')))
                continue;
            if (!long.TryParse(meta[3], out var size))
                throw Unparseable();
            entries.Add(new Entry(path, blob, size));
        }
        return entries;

        static KnapperException Unparseable() => new(VaultErrorCode.IoError,
            "unparseable `git ls-tree -z -l` entry — refusing to lint a baseline that was not fully read");
    }

    /// <summary>
    /// One `git cat-file --batch` process for a whole pass: a request per blob,
    /// answered in lockstep, so a vault's worth of notes costs one process
    /// rather than one each. Killed at <paramref name="deadline"/> (an
    /// Environment.TickCount64 value) so a git that stalls mid-object cannot
    /// hold the caller past its budget.
    /// </summary>
    public BlobReader OpenBlobs(long deadline)
    {
        RequireRepo();
        return new BlobReader(GitProcess.StartInfo(GitExecutable, vaultRoot, ["cat-file", "--batch"]), deadline);
    }

    public sealed class BlobReader : IDisposable
    {
        private readonly Process _process;
        private readonly Stream _stdout;
        private readonly Task<string> _stderr;
        private readonly Timer _watchdog;
        private int _killed;

        internal BlobReader(ProcessStartInfo psi, long deadline)
        {
            psi.RedirectStandardInput = true;
            _process = GitProcess.Start(psi);
            _stdout = _process.StandardOutput.BaseStream;
            // Drained concurrently for the reason GitProcess.Run gives: a git
            // writing warnings to a pipe nobody reads stops answering.
            _stderr = _process.StandardError.ReadToEndAsync();
            var remaining = Math.Max(0, deadline - Environment.TickCount64);
            _watchdog = new Timer(_ =>
            {
                Interlocked.Exchange(ref _killed, 1);
                GitProcess.Kill(_process);
            }, null, remaining, Timeout.Infinite);
        }

        /// <summary>The bytes of <paramref name="blob"/>, exactly as committed.</summary>
        public byte[] Read(string blob)
        {
            try
            {
                _process.StandardInput.Write(blob + "\n");
                _process.StandardInput.Flush();
                var header = ReadHeader();
                // "<name> blob <size>", or "<name> missing" for an absent object.
                var parts = header.Split(' ');
                if (parts.Length != 3 || parts[1] != "blob" || !int.TryParse(parts[2], out var size) || size < 0)
                    throw Failed($"unexpected `git cat-file --batch` answer for {blob}: '{header}'");
                var bytes = new byte[size];
                _stdout.ReadExactly(bytes);
                if (_stdout.ReadByte() != '\n')
                    throw Failed($"`git cat-file --batch` framing lost after {blob}");
                return bytes;
            }
            catch (Exception e) when (e is IOException or EndOfStreamException or ObjectDisposedException)
            {
                throw Failed(Volatile.Read(ref _killed) == 1
                    ? "reading the baseline tree exceeded its time budget and git was killed"
                    : $"`git cat-file --batch` stopped answering: {e.Message}");
            }
        }

        private string ReadHeader()
        {
            var line = new List<byte>(96);
            while (true)
            {
                var b = _stdout.ReadByte();
                if (b < 0)
                    throw new EndOfStreamException("git closed its output");
                if (b == '\n')
                    return Encoding.UTF8.GetString([.. line]);
                line.Add((byte)b);
            }
        }

        private KnapperException Failed(string what)
        {
            var stderr = _stderr.IsCompleted ? _stderr.GetAwaiter().GetResult().Trim() : "";
            return new KnapperException(Volatile.Read(ref _killed) == 1 ? VaultErrorCode.QueryTimeout : VaultErrorCode.IoError,
                stderr.Length == 0 ? what : $"{what} ({stderr})");
        }

        public void Dispose()
        {
            _watchdog.Dispose();
            try
            {
                _process.StandardInput.Close(); // EOF on stdin: --batch exits cleanly
                if (!_process.WaitForExit(2_000))
                    GitProcess.Kill(_process);
            }
            catch (Exception e) when (e is IOException or InvalidOperationException)
            {
                GitProcess.Kill(_process);
            }
            _process.Dispose();
        }
    }

    private void RequireRepo()
    {
        if (!RepoExists)
        {
            throw new KnapperException(VaultErrorCode.NotFound,
                "the vault is not a git repository, so there is no history to take a lint baseline from");
        }
    }

    private string Run(params string[] args) => GitProcess.Run(GitExecutable, vaultRoot, TimeoutMs, Consequence, args);
}
