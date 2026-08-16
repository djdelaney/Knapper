using System.Reflection;

namespace Knapper.Core;

/// <summary>
/// THE version string, for every surface that reports one: MCP
/// initialize.serverInfo.version, /health, /up, and `knapper version`. One
/// definition on purpose — an operator comparing what the tunnel reports
/// against what the CLI reports is doing a real check only if both numbers
/// come from the same place.
///
/// Read from AssemblyInformationalVersion, NOT AssemblyVersion. AssemblyVersion
/// is a four-part numeric: it silently drops everything that identifies the
/// build, so <c>0.2.0-rc.1</c> and <c>0.2.0+g1f5ff1c.dirty</c> both flatten to
/// "0.2.0" and a prerelease or an off-tag build reports itself as the release.
/// Directory.Build.props stamps the informational version (see
/// KnapperStampRevision); ops/version.sh computes it.
///
/// Read from THIS assembly, not the entry assembly: under `dotnet test` the
/// entry assembly is the test host, so an entry-assembly read reports the test
/// runner's version and every assertion about it tests nothing.
/// </summary>
public static class BuildInfo
{
    /// <summary>
    /// e.g. <c>0.2.0+g1f5ff1c</c>, or <c>0.2.0</c> when built outside a git
    /// checkout. Never empty: a surface that reports no version at all reads
    /// as a broken response rather than an unidentifiable build.
    /// </summary>
    public static string Version { get; } = Resolve();

    /// <summary>
    /// The release number alone (<c>0.2.0</c>) — the part that is compared
    /// against a tag. Everything after '+' identifies the build, not the release.
    /// </summary>
    public static string Release => Version.Split('+')[0];

    /// <summary>
    /// Orders two version strings by their RELEASE part alone: negative if
    /// <paramref name="left"/> is older, 0 if the same release, positive if
    /// newer — and <c>null</c> when either string is not an X.Y.Z release,
    /// which is a THIRD answer, not a tie. "Cannot be ordered" and "the same
    /// age" lead to opposite conclusions in the one place this is used
    /// (deciding whether a version mismatch is a stale client or a bad
    /// deployment), so they must not share a return value.
    ///
    /// Build metadata after '+' is deliberately ignored: it identifies a
    /// build, not an age, and two builds of one release are the same release.
    /// </summary>
    public static int? CompareReleases(string left, string right)
    {
        var a = Parse(left);
        var b = Parse(right);
        if (a is null || b is null)
            return null;
        for (var i = 0; i < 3; i++)
        {
            var c = a[i].CompareTo(b[i]);
            if (c != 0)
                return c;
        }
        return 0;

        static int[]? Parse(string version)
        {
            var parts = version.Split('+')[0].Split('.');
            if (parts.Length != 3)
                return null;
            var numbers = new int[3];
            for (var i = 0; i < 3; i++)
            {
                if (!int.TryParse(parts[i], out numbers[i]))
                    return null;
            }
            return numbers;
        }
    }

    private static string Resolve()
    {
        var informational = typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
            return informational;
        // Only reachable if assembly-info generation was turned off. Fall back
        // to the numeric version rather than reporting nothing.
        return typeof(BuildInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
