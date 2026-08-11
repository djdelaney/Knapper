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
}
