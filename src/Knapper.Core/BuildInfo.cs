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
