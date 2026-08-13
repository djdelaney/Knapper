using Knapper.Core.Query;

namespace Knapper.Core.Tests.Query;

/// <summary>
/// The gate that keeps an rg whose summary stats under-report off a
/// production vault. The version strings below are real `rg --version`
/// output, not invented shapes.
/// </summary>
public sealed class RipgrepVersionTests
{
    // Captured from the actual binaries: 14.1.1 is what Debian/apt ships and
    // what the CI runner had before it was pinned; 15.2.0 is the first
    // generation that counts matchless searches.
    private const string Rg14 = "ripgrep 14.1.1 (rev 4649aa9700)";
    private const string Rg15 = "ripgrep 15.2.0 (rev e89fff89ac)";

    [Theory]
    [InlineData(Rg14, 14)]
    [InlineData(Rg15, 15)]
    [InlineData("ripgrep 13.0.0\n-SIMD -AVX (compiled)\n+SIMD +AVX (runtime)", 13)]
    [InlineData("ripgrep 15.2.0", 15)]
    [InlineData("  ripgrep 16.0.1 (rev abc)  ", 16)]
    public void Major_version_is_read_from_real_version_output(string output, int expected) =>
        RipgrepVersion.ParseMajor(output).ShouldBe(expected);

    [Theory]
    [InlineData("")]
    [InlineData("command not found")]
    [InlineData("rg 15.2.0")]            // not the "ripgrep" prefix rg actually prints
    [InlineData("ripgrep vNext")]
    public void Unrecognizable_output_is_unknown_not_assumed_good(string output)
    {
        // Null means "could not tell", and IsSupported must refuse on it —
        // guessing "probably fine" is how a degraded rg reaches a vault.
        RipgrepVersion.ParseMajor(output).ShouldBeNull();
        RipgrepVersion.IsSupported(output).ShouldBeFalse();
    }

    [Fact]
    public void The_apt_generation_is_refused_and_the_pinned_one_accepted()
    {
        RipgrepVersion.IsSupported(Rg14).ShouldBeFalse();
        RipgrepVersion.IsSupported(Rg15).ShouldBeTrue();
    }

    [Fact]
    public void The_minimum_is_the_release_that_fixed_the_summary_stats() =>
        RipgrepVersion.MinimumMajor.ShouldBe(15);

    /// <summary>
    /// Whatever rg is on this machine must satisfy the gate — otherwise the
    /// rest of the query suite is asserting against an unsupported engine.
    /// </summary>
    [Fact]
    public void The_rg_used_by_this_test_run_is_supported()
    {
        var probe = RipgrepVersion.Read("rg");

        probe.Error.ShouldBeNull();
        RipgrepVersion.IsSupported(probe.Output!).ShouldBeTrue(
            $"the rg on PATH is not {RipgrepVersion.MinimumMajor}+: '{probe.Output!.Split('\n')[0].Trim()}'");
    }

    [Fact]
    public void A_missing_binary_reports_an_error_rather_than_throwing()
    {
        // Both callers — doctor and server startup — rely on this never
        // throwing: one turns it into a failed check, the other into a
        // warning, and an escaped exception would take the server down at boot
        // over a diagnostic.
        var probe = RipgrepVersion.Read("/nonexistent/rg");

        probe.Output.ShouldBeNull();
        probe.Error.ShouldNotBeNull();
        probe.ResolvedPath.ShouldBeNull();
        // An explicit path searches no PATH, and saying it did would send the
        // operator off editing the wrong thing.
        probe.SearchPath.ShouldBeNull();
    }

    /// <summary>
    /// The probe says WHICH binary answered. On CT 106 `doctor` reported
    /// `rg → not found` while /health on the same box reported ripgrep 15.2.0:
    /// the service inherits systemd's manager PATH (which has /usr/local/bin),
    /// the operator's `pct exec` shell does not. Read alone, that FAIL says the
    /// release broke ripgrep detection, and the obvious response is a rollback.
    /// </summary>
    [Fact]
    public void A_bare_command_reports_the_absolute_binary_that_answered()
    {
        var probe = RipgrepVersion.Read("rg");

        probe.Error.ShouldBeNull();
        probe.ResolvedPath.ShouldNotBeNull();
        Path.IsPathRooted(probe.ResolvedPath).ShouldBeTrue();
        File.Exists(probe.ResolvedPath).ShouldBeTrue();
        // The rg in use is named on the ok line, so "is the pinned build the
        // one running?" is answered by reading doctor rather than inferring it
        // from a version number.
        RipgrepVersion.Describe("rg", probe).ShouldStartWith(probe.ResolvedPath!);
    }

    [Fact]
    public void A_command_that_is_not_on_PATH_reports_the_PATH_it_searched()
    {
        var probe = RipgrepVersion.Read("knapper-no-such-command");

        probe.ResolvedPath.ShouldBeNull();
        probe.SearchPath.ShouldBe(Environment.GetEnvironmentVariable("PATH"));
        // The PATH is IN the message: that string is the whole diagnosis, and
        // an operator reading only the FAIL line must not have to go get it.
        // `doctor` appends Error to its label, so the FAIL line carries it.
        probe.Error.ShouldNotBeNull();
        probe.Error!.ShouldContain("PATH=");
        // Describe contributes the location only — saying the reason twice on
        // one line reads like two separate problems.
        RipgrepVersion.Describe("knapper-no-such-command", probe).ShouldBe("not found");
    }

    [Fact]
    public void A_file_that_exists_but_is_not_executable_is_not_a_ripgrep()
    {
        // The same predicate drives the PATH walk, which is why it matters that
        // it reads the mode rather than mere existence: a shell keeps searching
        // past a non-executable hit, and a stray data file named `rg` early in
        // PATH would otherwise turn a working deployment into a permission
        // error with no explanation attached. Asserted through the explicit-path
        // branch so this test never touches the process-wide PATH.
        var directory = Directory.CreateTempSubdirectory("knapper-rg-probe").FullName;
        try
        {
            var decoy = Path.Combine(directory, "rg");
            File.WriteAllText(decoy, "not a binary\n");
            File.SetUnixFileMode(decoy, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var probe = RipgrepVersion.Read(decoy);

            probe.Output.ShouldBeNull();
            probe.ResolvedPath.ShouldBeNull();
            probe.Error.ShouldNotBeNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_binary_that_is_not_ripgrep_is_not_mistaken_for_one()
    {
        // Exits 0 and prints something, just not a ripgrep version banner.
        var probe = RipgrepVersion.Read("/bin/echo");

        probe.Error.ShouldBeNull();
        RipgrepVersion.IsSupported(probe.Output!).ShouldBeFalse();
    }
}
