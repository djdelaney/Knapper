using System.Diagnostics;
using System.Text;

namespace Knapper.Core.Git;

/// <summary>
/// The ONE way Knapper starts git: structured args, never a shell, with the
/// environment and per-call overrides that stop anything on disk choosing a
/// program for git to run. <see cref="GitCommitJob"/> (the committer) and
/// <see cref="GitTreeReader"/> (lint's baseline reader) both start git here, so
/// a key added to <see cref="NeutralizedConfig"/> covers every invocation.
/// </summary>
internal static class GitProcess
{
    /// <summary>
    /// Per-invocation overrides for every config key the commands Knapper runs
    /// would EXECUTE: hooks (pre-commit, commit-msg, post-commit…), the
    /// fsmonitor daemon command `add` consults, and a commit-signing program.
    /// </summary>
    internal static readonly string[] NeutralizedConfig =
    [
        "core.hooksPath=/dev/null",
        "core.fsmonitor=false",
        "commit.gpgSign=false",
    ];

    /// <summary>A start description for git in <paramref name="root"/>: neutralized, stdout/stderr redirected.</summary>
    internal static ProcessStartInfo StartInfo(string git, string root, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = git,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Nothing on disk may choose code for git to run. Knapper holds the
        // commit lock and writes /var/lib/knapper, but `.git/` is writable by
        // whatever else runs as the service account — obsidian-headless, a
        // networked npm program confined to /vault and /home/knapper. A hook,
        // an fsmonitor command or a signing program it planted there would run
        // HERE, outside its own sandbox. System and global config are cut off
        // entirely; the repo's own config is needed (identity), so the keys
        // that name a program are overridden per call, and filter drivers —
        // which have arbitrary names and cannot be overridden that way — are
        // refused before `add` could run one (GitCommitJob.RequireNoFilterDrivers).
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        // Replace refs (`.git/refs/replace/<sha>`) silently substitute one
        // object for another on READ, and `.git/` is writable by that same
        // account. Lint's baseline pins a commit OUTSIDE the vault precisely so
        // nothing lint reports on can move it; honoring a planted replacement
        // would let the tree it names be swapped anyway, absorbing every live
        // finding into the "accepted" backlog. (Rewriting loose objects in
        // place is the residual: cat-file does not re-hash what it reads.)
        psi.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        foreach (var setting in NeutralizedConfig)
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(setting);
        }
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(root);
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>
    /// Run git to completion and return stdout; a non-zero exit throws
    /// IoError carrying git's stderr. See <see cref="Execute"/> for the
    /// bounds; <paramref name="consequence"/> finishes the timeout sentence
    /// ("the commit is abandoned…").
    /// </summary>
    internal static string Run(string git, string root, int timeoutMs, string consequence, params string[] args)
    {
        var (exitCode, stdout, stderr) = Execute(git, root, timeoutMs, consequence, args);
        if (exitCode != 0)
        {
            throw new KnapperException(VaultErrorCode.IoError,
                $"git {args[0]} failed ({exitCode}): {stderr.Trim()}");
        }
        return stdout;
    }

    /// <summary>
    /// Run git to completion and hand back its exit code, for the commands
    /// whose non-zero exit is an ANSWER (`cat-file -e`: the object is absent)
    /// rather than a failure. Both pipes are drained CONCURRENTLY and the wait
    /// is BOUNDED; a timeout throws, because it answers nothing.
    /// </summary>
    internal static (int ExitCode, string Stdout, string Stderr) Execute(
        string git, string root, int timeoutMs, string consequence, params string[] args)
    {
        var psi = StartInfo(git, root, args);
        psi.StandardOutputEncoding = Encoding.UTF8;

        using var process = Start(psi);
        // Draining one pipe to EOF before starting the other deadlocks the
        // moment git emits more than a pipe buffer on the stream nobody is
        // reading: the child blocks writing stderr, the parent blocks reading
        // stdout, and neither ever moves. The bound covers the rest — a git
        // that hangs without filling a pipe at all (a stalled filesystem, an
        // index.lock it decides to wait on). The committer runs under the
        // vault-wide commit lock, which every mutation needs in shared mode,
        // so for it an unbounded wait is a wedged vault with no caller in a
        // position to time it out.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            Kill(process);
            throw new KnapperException(VaultErrorCode.IoError,
                $"git {args[0]} did not exit within {timeoutMs} ms and was killed — {consequence}");
        }
        // WaitForExit(int) does not itself await the redirected streams, and
        // this wait is bounded for the same reason the one above is: a
        // grandchild inheriting the pipe holds it open past git's own exit,
        // and an unbounded wait here would hand back the wedge the timeout
        // just removed.
        if (!Task.WaitAll([stdoutTask, stderrTask], timeoutMs))
        {
            throw new KnapperException(VaultErrorCode.IoError,
                $"git {args[0]} exited but its output pipes stayed open past {timeoutMs} ms — {consequence}");
        }
        return (process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
    }

    /// <summary>
    /// Start git, TYPED on failure: a missing or unexecutable binary is
    /// Win32Exception from Process.Start, which would otherwise reach a
    /// client as an untyped internal error instead of an IoError naming git.
    /// </summary>
    internal static Process Start(ProcessStartInfo psi)
    {
        try
        {
            return Process.Start(psi)
                ?? throw new KnapperException(VaultErrorCode.IoError, "failed to start git");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new KnapperException(VaultErrorCode.IoError, $"git could not be started ({psi.FileName}): {e.Message}", e);
        }
    }

    internal static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException
            or System.ComponentModel.Win32Exception)
        {
            // Already gone, or unkillable. Either way the caller's throw is the answer.
        }
    }
}
