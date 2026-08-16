using System.Text.RegularExpressions;

namespace Knapper.Core.Tests;

/// <summary>
/// The version is a deployment CONTRACT, not a cosmetic string: `knapper verify
/// --expect-version` is the only check standing between "the service restarted"
/// and "the service restarted onto the binary you just installed", and it can
/// only work if what the binaries report is derived from <Version> and carries
/// the build identity.
///
/// Every failure mode here is silent. Reverting BuildInfo to AssemblyVersion
/// still yields a plausible "0.2.0" — with the revision, and therefore the
/// dirty-tree signal, quietly gone. Adding a second version carrier gives the
/// two a way to disagree while both look authoritative. Neither breaks a build
/// or a wire test; both make the deployment check assert nothing.
/// </summary>
public sealed class VersionSurfaceTests
{
    [Fact]
    public void BuildInfo_reports_the_version_from_Directory_Build_props()
    {
        if (RepoRoot() is not { } root)
            return; // not a source checkout (published artifact) — nothing to compare against

        BuildInfo.Release.ShouldBe(
            DeclaredVersion(root),
            "the version the binaries report has drifted from <Version> in Directory.Build.props. " +
            "Bump with ops/release.sh, which is what keeps the property, the commit and the tag together.");
    }

    [Fact]
    public void BuildInfo_carries_the_build_identity_not_just_the_release()
    {
        // AssemblyVersion is four numeric parts and cannot hold this suffix, so
        // this assertion is exactly what fails if BuildInfo is "simplified" back
        // to reading it. Skipped outside a git checkout, where ops/version.sh
        // deliberately yields a bare release (see its FAIL-SOFT note).
        if (RepoRoot() is not { } root || !Directory.Exists(Path.Combine(root, ".git")))
            return;

        BuildInfo.Version.StartsWith(BuildInfo.Release + "+", StringComparison.Ordinal).ShouldBeTrue(
            $"'{BuildInfo.Version}' carries no build revision. Directory.Build.props' KnapperStampRevision " +
            "target writes it; without it every build of a release reports an identical version, and a " +
            "binary built off uncommitted edits is indistinguishable from the tagged one at every surface.");

        // Semver build metadata: dot-separated alphanumerics after the '+'.
        // The exact shape is ops/version.sh's contract (g<short sha>[.dirty]).
        Regex.IsMatch(BuildInfo.Version, @"^\d+\.\d+\.\d+\+g[0-9a-f]{7}(\.dirty)?$").ShouldBeTrue(
            $"'{BuildInfo.Version}' is not the shape ops/version.sh produces (X.Y.Z+g<sha7>[.dirty])");
    }

    [Fact]
    public void Release_strips_the_build_metadata_so_it_can_be_compared_to_a_tag()
    {
        // What `verify --expect-version 0.2.0` compares. If Release stopped
        // splitting on '+', an operator's bare release string would never match
        // a stamped build and the check would fail on every correct deployment
        // — which trains people to stop passing the flag.
        BuildInfo.Release.ShouldNotContain("+");
        BuildInfo.Version.StartsWith(BuildInfo.Release, StringComparison.Ordinal).ShouldBeTrue();
    }

    [Fact]
    public void CompareReleases_orders_by_release_and_says_when_it_cannot()
    {
        // Two builds of one release are the SAME release: the metadata after
        // '+' identifies a build, not an age.
        BuildInfo.CompareReleases("0.5.1+gabc0dbf", "0.5.1+g9ebbc48").ShouldBe(0);
        (BuildInfo.CompareReleases("0.3.2+g9ebbc48", "0.5.1+gabc0dbf") is < 0).ShouldBeTrue();
        (BuildInfo.CompareReleases("0.5.1", "0.4.9") is > 0).ShouldBeTrue();
        // Numeric, not lexical: "0.10.0" is newer than "0.9.0".
        (BuildInfo.CompareReleases("0.9.0", "0.10.0") is < 0).ShouldBeTrue();

        // Unorderable is a THIRD answer. `verify` decides whether a version
        // mismatch is a stale client or a bad deployment from this, and
        // "cannot tell" must not arrive looking like "same age" — one adds a
        // sentence sending an operator to fix their own box, the other leaves
        // the neutral diagnosis standing.
        BuildInfo.CompareReleases("0.5.1", "not-a-version").ShouldBeNull();
        BuildInfo.CompareReleases("0.5", "0.5.1").ShouldBeNull();
        BuildInfo.CompareReleases("0.5.1-rc.1", "0.5.1").ShouldBeNull();
    }

    [Fact]
    public void Directory_Build_props_declares_exactly_one_version()
    {
        if (RepoRoot() is not { } root)
            return;

        // ops/version.sh, ops/publish.sh and ops/release.sh each read the
        // property by pattern. A second <Version> element — from a merge, or
        // from someone adding a per-configuration override — resolves to
        // whichever line a given consumer reads first, and they need not agree.
        var text = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        Regex.Matches(text, @"<Version>[^<]*</Version>").Count.ShouldBe(
            1, "Directory.Build.props must declare exactly one <Version> element");
    }

    /// <summary>The one carrier, read the way the shell tooling reads it.</summary>
    private static string DeclaredVersion(string root)
    {
        var text = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        var match = Regex.Match(text, @"<Version>([^<]*)</Version>");
        match.Success.ShouldBeTrue("Directory.Build.props declares no <Version>");
        return match.Groups[1].Value;
    }

    /// <summary>Walks up from the test binary to the checkout; null when there isn't one.</summary>
    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Knapper.slnx")))
            dir = dir.Parent;
        return dir?.FullName;
    }
}
