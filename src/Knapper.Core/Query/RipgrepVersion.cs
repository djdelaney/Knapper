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
    /// </summary>
    public readonly record struct Probe(string? Output, string? Error);

    /// <summary>
    /// Run <c>rg --version</c>. Shared by `knapper doctor` (which turns a bad
    /// result into a failed check) and server startup (which warns), so the two
    /// can never disagree about what counts as a usable ripgrep.
    /// </summary>
    public static Probe Read(string ripgrepPath)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = ripgrepPath, RedirectStandardOutput = true };
            psi.ArgumentList.Add("--version");
            using var process = Process.Start(psi);
            if (process is null)
                return new Probe(null, $"could not start '{ripgrepPath}'");
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
                return new Probe(null, $"'{ripgrepPath} --version' did not exit within 5s");
            }
            return process.ExitCode == 0
                ? new Probe(output, null)
                : new Probe(null, $"'{ripgrepPath} --version' exited {process.ExitCode}");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new Probe(null, e.Message);
        }
    }
}
