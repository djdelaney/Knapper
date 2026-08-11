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
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "rg",
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("--version");
        using var process = System.Diagnostics.Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);

        RipgrepVersion.IsSupported(output).ShouldBeTrue(
            $"the rg on PATH is not {RipgrepVersion.MinimumMajor}+: '{output.Split('\n')[0].Trim()}'");
    }
}
