using Knapper.Core.Options;

namespace Knapper.Mcp.Tests;

/// <summary>
/// <see cref="AccessOptions.Validate"/> is the boot gate for the ingress
/// config: everything it misses becomes a running server with a weaker auth
/// posture than its settings appear to describe. The audience pair matters
/// most — <c>/up</c> accepts the monitoring audience, the vault surface does
/// not, and that asymmetry is only real while the two values differ.
/// </summary>
public sealed class AccessOptionsTests
{
    private static AccessOptions Coherent() => new()
    {
        Enabled = true,
        TeamDomain = "https://knapper-test.cloudflareaccess.com",
        Audience = "aud-owner",
    };

    [Fact]
    public void A_coherent_configuration_validates() =>
        Coherent().Validate().ShouldBeNull();

    [Fact]
    public void A_disabled_gate_validates_regardless_of_the_rest() =>
        new AccessOptions { Enabled = false, TeamDomain = "", Audience = "" }
            .Validate().ShouldBeNull();

    [Fact]
    public void An_empty_team_domain_is_refused()
    {
        var options = Coherent();
        options.TeamDomain = "";
        options.Validate().ShouldNotBeNull().ShouldContain("TeamDomain is empty");
    }

    [Fact]
    public void A_plaintext_team_domain_is_refused()
    {
        // The signing keys are fetched from this origin.
        var options = Coherent();
        options.TeamDomain = "http://knapper-test.cloudflareaccess.com";
        options.Validate().ShouldNotBeNull().ShouldContain("absolute https:// URL");
    }

    [Fact]
    public void An_empty_audience_is_refused()
    {
        var options = Coherent();
        options.Audience = "";
        options.Validate().ShouldNotBeNull().ShouldContain("Audience is empty");
    }

    [Fact]
    public void A_monitoring_audience_equal_to_the_owner_audience_is_refused()
    {
        var options = Coherent();
        options.MonitoringAudience = options.Audience;
        options.Validate().ShouldNotBeNull().ShouldContain("would carry the whole vault surface");
    }

    [Fact]
    public void An_empty_monitoring_audience_is_accepted_and_narrows_to_the_owner()
    {
        // The single-app setup: supported, so Validate() accepts it — but it is
        // a DOWNGRADE, not a neutral choice, because /up then accepts the owner
        // audience and the monitor authenticates with the vault's own token.
        //
        // Note how differently the two ways of getting this wrong behave, and
        // that the safer-looking one is the trap: an EQUAL audience (above)
        // refuses startup outright, while an empty one boots clean with every
        // surface green. Startup logs a warning, which is the only signal there
        // is — see Program.cs, and knapper.service ships a commented
        // placeholder for this key so it is not reached by doing nothing.
        var options = Coherent();
        options.MonitoringAudience = "";

        options.Validate().ShouldBeNull();
        options.MonitoringAudiences().ShouldBe(["aud-owner"]);
    }

    [Fact]
    public void A_distinct_monitoring_audience_is_accepted_and_stays_off_the_vault_surface()
    {
        var options = Coherent();
        options.MonitoringAudience = "aud-monitor";

        options.Validate().ShouldBeNull();
        options.MonitoringAudiences().ShouldBe(["aud-owner", "aud-monitor"]);
        options.OwnerAudiences().ShouldBe(["aud-owner"]);
    }
}
