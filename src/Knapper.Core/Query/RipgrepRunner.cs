using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Knapper.Core.Query;

/// <summary>
/// Executes ripgrep with STRUCTURED arguments — <c>ArgumentList</c>, never a
/// shell, never string-concatenated commands (brief §6: constrained query
/// API, not remote shell). Streams stdout lines to a callback that can stop
/// early when a budget fills; enforces the wall-clock budget by killing the
/// process. Every rg invocation in the codebase goes through here.
/// </summary>
internal sealed class RipgrepRunner(string ripgrepPath)
{
    /// <summary>
    /// Baseline flags for every invocation. --no-config: a user config could
    /// add --hidden or ignore rules and silently change the contract.
    /// --no-ignore: vault CONTENT must not steer the search — a note shipping
    /// a .rgignore/.gitignore would otherwise hide files from "exhaustively
    /// searched" scopes. --no-follow: symlinks are rejected everywhere.
    /// --sort=path: deterministic page order (single-threaded; the vault is
    /// small by design and correctness beats parallelism here).
    /// Hidden files stay excluded (rg's default), matching the file lister.
    /// </summary>
    internal static readonly string[] BaselineArgs =
        ["--no-config", "--no-ignore", "--no-follow", "--sort=path"];

    internal sealed record Outcome(bool Completed, bool TimedOut, bool StoppedEarly, int ExitCode, string StdErr);

    /// <summary>
    /// Per-line materialization cap. One pathological matching line (a log
    /// export, minified JSON) would otherwise be built into a string in full
    /// before any downstream byte budget can apply. Exceeding it is a TYPED
    /// refusal — never a silent truncation that would forge completeness.
    /// Far above any real note line; the JSON envelope of a match roughly
    /// doubles the content, so this bounds memory at a few MiB.
    /// </summary>
    private const int MaxLineChars = 2 * 1024 * 1024;

    /// <summary>
    /// Run rg in <paramref name="workingDirectory"/>. <paramref name="onLine"/>
    /// returns false to stop early (budget filled) — the process is killed and
    /// the outcome reports <c>StoppedEarly</c>. Exit code 2 without early stop
    /// is surfaced as a typed <see cref="VaultErrorCode.InvalidArgument"/>
    /// (rg's own message included — it names bad regexes precisely).
    /// </summary>
    internal Outcome Run(
        IReadOnlyList<string> args,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken ct,
        Func<string, bool> onLine)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ripgrepPath,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in BaselineArgs)
            psi.ArgumentList.Add(a);
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        Process process;
        try
        {
            process = Process.Start(psi)
                ?? throw new KnapperException(VaultErrorCode.IoError, "failed to start ripgrep");
        }
        catch (Win32Exception e)
        {
            throw new KnapperException(VaultErrorCode.IoError,
                $"cannot execute ripgrep at '{ripgrepPath}' — is ripgrep installed and on PATH? ({e.Message})");
        }

        using (process)
        {
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var stoppedEarly = false;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            using var killOnCancel = timeoutCts.Token.Register(() => TryKill(process));

            var sinkThrew = true;
            try
            {
                while (ReadLineBounded(process.StandardOutput, process) is { } line)
                {
                    if (!onLine(line))
                    {
                        stoppedEarly = true;
                        TryKill(process);
                        break;
                    }
                }
                sinkThrew = false;
            }
            catch (IOException)
            {
                // Stream torn down by the kill — the flags below say which.
                sinkThrew = false;
            }
            finally
            {
                // A sink (onLine) exception must not leave rg alive until
                // SIGPIPE, and the wait must be bounded — an unkillable rg
                // must surface as an error, not a hung request thread.
                if (sinkThrew)
                    TryKill(process);
                if (!process.WaitForExit(10_000))
                {
                    TryKill(process);
                    if (!process.WaitForExit(5_000) && !sinkThrew)
                    {
                        throw new KnapperException(VaultErrorCode.IoError,
                            "ripgrep did not exit after being killed — refusing to report a result for a query still running");
                    }
                }
            }
            ct.ThrowIfCancellationRequested(); // caller cancellation surfaces as OCE

            var timedOut = timeoutCts.IsCancellationRequested && !stoppedEarly;
            var stderr = stderrTask.GetAwaiter().GetResult();
            if (!timedOut && !stoppedEarly && process.ExitCode == 2)
            {
                throw new KnapperException(VaultErrorCode.InvalidArgument,
                    $"ripgrep rejected the query: {Truncate(stderr.Trim(), 500)}");
            }
            return new Outcome(
                Completed: !timedOut && !stoppedEarly,
                TimedOut: timedOut,
                StoppedEarly: stoppedEarly,
                ExitCode: process.HasExited ? process.ExitCode : -1,
                StdErr: stderr);
        }
    }

    /// <summary>
    /// ReadLine with the materialization cap: kills rg and throws typed
    /// TooLarge instead of building an unbounded string. '\n' terminates;
    /// one trailing '\r' is stripped (rg on Unix emits none, but a note's
    /// own CRLF content can reach -l/count output paths).
    /// </summary>
    private static string? ReadLineBounded(StreamReader reader, Process process)
    {
        var sb = new StringBuilder(256);
        int ci;
        while ((ci = reader.Read()) >= 0)
        {
            var c = (char)ci;
            if (c == '\n')
            {
                if (sb.Length > 0 && sb[^1] == '\r')
                    sb.Length--;
                return sb.ToString();
            }
            sb.Append(c);
            if (sb.Length > MaxLineChars)
            {
                TryKill(process);
                throw new KnapperException(VaultErrorCode.TooLarge,
                    $"a matching line exceeds the per-line cap ({MaxLineChars} chars) — the file is not " +
                    "note-shaped (log export? minified blob?); narrow the pattern or exclude the file");
            }
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (SystemException) { }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
