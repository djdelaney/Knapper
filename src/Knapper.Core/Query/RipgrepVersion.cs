using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Knapper.Core.Query;

/// <summary>
/// The ripgrep version is part of the query contract, not a packaging detail.
///
/// Asked for a pattern that matches nothing, rg 14 and earlier report
/// <c>"searches": 0</c> and <c>"bytes_searched": 0</c> in the JSON summary;
/// rg 15 reports the files it actually examined. <c>scannedFiles</c> is read
/// from those stats, and it is the evidence that "no match" means the scope
/// was exhaustively searched rather than that nothing was looked at. On an
/// older rg the envelope keeps claiming <c>truncated: false</c> while its
/// only supporting number collapses to zero — a degradation with no error
/// attached, which is why `knapper doctor` FAILS on it rather than warning.
/// </summary>
public static partial class RipgrepVersion
{
    /// <summary>Oldest rg whose JSON summary counts matchless searches.</summary>
    public const int MinimumMajor = 15;

    /// <summary>`rg --version` leads with "ripgrep &lt;major&gt;.&lt;minor&gt;.&lt;patch&gt;", optionally
    /// followed by a revision and feature lines.</summary>
    [GeneratedRegex(@"^ripgrep\s+(\d+)\.\d+", RegexOptions.IgnoreCase)]
    private static partial Regex VersionLine();

    /// <summary>
    /// Major version from <c>rg --version</c> output, or null when the output
    /// is not recognizable — null is "unknown", never "assume it is fine".
    /// </summary>
    public static int? ParseMajor(string versionOutput)
    {
        foreach (var line in versionOutput.Split('\n'))
        {
            var match = VersionLine().Match(line.Trim());
            if (match.Success && int.TryParse(match.Groups[1].Value, out var major))
                return major;
        }
        return null;
    }

    /// <summary>True when this rg counts matchless searches in its summary stats.</summary>
    public static bool IsSupported(string versionOutput) =>
        ParseMajor(versionOutput) is { } major && major >= MinimumMajor;

    /// <summary>
    /// Outcome of running <c>rg --version</c>: either <paramref name="Output"/>
    /// or an <paramref name="Error"/> saying why not. Never both, never neither.
    ///
    /// <paramref name="ResolvedPath"/> is WHICH binary answered — absolute, and
    /// the one actually executed, not a guess about what a later exec might
    /// pick. It exists because "not found" is far more often an invocation
    /// problem than a missing binary: on CT 106 a `doctor` run under
    /// <c>pct exec</c> (PATH <c>/sbin:/bin:/usr/sbin:/usr/bin</c>) reported
    /// `rg → not found` while /health on the SAME box reported ripgrep 15.2.0,
    /// because the service inherits systemd's manager PATH, which includes
    /// <c>/usr/local/bin</c>. Read alone that FAIL says the release broke
    /// ripgrep detection, and the obvious response is a rollback.
    /// <paramref name="SearchPath"/> is the PATH that was searched (null when
    /// the configured name already contained a directory, so nothing was), and
    /// it is what turns that confusing FAIL into a self-explaining one.
    /// </summary>
    public readonly record struct Probe(string? Output, string? Error, string? ResolvedPath, string? SearchPath);

    /// <summary>
    /// Run <c>rg --version</c>. Shared by `knapper doctor` (which turns a bad
    /// result into a failed check) and server startup (which warns), so the two
    /// can never disagree about what counts as a usable ripgrep.
    /// </summary>
    public static Probe Read(string ripgrepPath)
    {
        var (resolved, searchPath) = Locate(ripgrepPath);
        if (resolved is null)
        {
            // Named a directory: the shell would say "no such file", and so do
            // we. Named a bare command: say what was searched — the answer is
            // almost always that the caller's PATH is not the service's.
            return new Probe(
                null,
                searchPath is null
                    ? $"'{ripgrepPath}' does not exist or is not executable"
                    : $"'{ripgrepPath}' is not on PATH={searchPath}",
                null,
                searchPath);
        }
        try
        {
            // The RESOLVED path is what gets executed, so ResolvedPath below
            // names the binary that actually answered rather than one a second,
            // independent lookup might have picked.
            var psi = new ProcessStartInfo { FileName = resolved, RedirectStandardOutput = true };
            psi.ArgumentList.Add("--version");
            using var process = Process.Start(psi);
            if (process is null)
                return new Probe(null, $"could not start '{resolved}'", resolved, searchPath);
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already gone between the timeout and the kill.
                }
                return new Probe(null, $"'{resolved} --version' did not exit within 5s", resolved, searchPath);
            }
            return process.ExitCode == 0
                ? new Probe(output, null, resolved, searchPath)
                : new Probe(null, $"'{resolved} --version' exited {process.ExitCode}", resolved, searchPath);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new Probe(null, e.Message, resolved, searchPath);
        }
    }

    /// <summary>
    /// One line saying which binary answered and what it said — the thing
    /// `knapper doctor` prints, so a pass names the rg in use (confirming the
    /// pinned <c>/usr/local/bin/rg</c> rather than an apt build, which
    /// otherwise has to be inferred from the version number) and a failure
    /// names where it looked.
    /// </summary>
    public static string Describe(string configuredPath, Probe probe)
    {
        var first = probe.Output?.Split('\n')[0].Trim();
        // No arrow when the configured value IS the resolved one — repeating an
        // absolute path back at the reader adds nothing.
        var where = probe.ResolvedPath is { } resolved && resolved != configuredPath ? $"{resolved} → " : "";
        if (first is not null)
            return $"{where}{first}";
        // On a failure this contributes the LOCATION only. The reason lives in
        // Error, and callers append that themselves — printing it twice on one
        // line reads like two separate problems.
        return probe.ResolvedPath is null ? "not found" : $"{probe.ResolvedPath} → no version";
    }

    /// <summary>
    /// PATH lookup, the way a shell does it: a name containing a directory
    /// separator is used as given, anything else is searched along PATH and
    /// non-executable hits are skipped rather than accepted. Unix-only by
    /// repo policy, hence ':' and '/' rather than the platform separators.
    /// </summary>
    private static (string? Resolved, string? SearchPath) Locate(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return (null, null);
        if (command.Contains('/', StringComparison.Ordinal))
            return (IsExecutable(command) ? Path.GetFullPath(command) : null, null);

        var searchPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in searchPath.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, command);
            if (IsExecutable(candidate))
                return (Path.GetFullPath(candidate), searchPath);
        }
        return (null, searchPath);
    }

    private static bool IsExecutable(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            const UnixFileMode anyExecute =
                UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(path) & anyExecute) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
