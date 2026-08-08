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

            try
            {
                while (process.StandardOutput.ReadLine() is { } line)
                {
                    if (!onLine(line))
                    {
                        stoppedEarly = true;
                        TryKill(process);
                        break;
                    }
                }
            }
            catch (IOException)
            {
                // Stream torn down by the kill — the flags below say which.
            }
            process.WaitForExit();
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
