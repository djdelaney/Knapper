using Knapper.Core.Options;
using Knapper.Core.Transfer;

namespace Knapper.Core.Tests.Transfer;

/// <summary>
/// The ONE validator startup and doctor share (transfer-grants §10, §8.3,
/// §13). Each case is a configuration that must refuse to boot.
/// </summary>
public class TransferConfigurationTests
{
    private const long SyncMax = 5 * 1024 * 1024;

    private static McpOptions EdgeFronted() => new()
    {
        AllowedHosts = ["vault.example.com"],
        Access = new AccessOptions { Enabled = true },
    };

    private static DownloadOptions On() => new() { Enabled = true, AllowedPrefixes = ["Photos"] };

    private static TransferOptions Origin(string origin, bool localOnly = false) => new() { PublicOrigin = origin, LocalOnly = localOnly };

    private static IReadOnlyList<string> Validate(
        TransferOptions? t = null, DownloadOptions? d = null, UploadOptions? u = null, McpOptions? m = null) =>
        TransferConfiguration.Validate(t ?? new TransferOptions(), d ?? new DownloadOptions(), u ?? new UploadOptions(),
            m ?? new McpOptions(), SyncMax);

    [Fact]
    public void Everything_off_is_coherent() => Validate().ShouldBeEmpty();

    [Fact]
    public void A_production_shaped_download_configuration_is_coherent() =>
        Validate(Origin("https://vault.example.com"), On(), m: EdgeFronted()).ShouldBeEmpty();

    [Fact]
    public void A_local_only_download_configuration_is_coherent() =>
        Validate(Origin("http://127.0.0.1:3535", localOnly: true), On()).ShouldBeEmpty();

    [Fact]
    public void Upload_cannot_be_enabled_before_it_is_built() =>
        Validate(u: new UploadOptions { Enabled = true }).ShouldHaveSingleItem().ShouldContain("not built");

    [Fact]
    public void A_read_only_deployment_refuses_downloads()
    {
        var mcp = EdgeFronted();
        mcp.ReadOnly = true;
        Validate(Origin("https://vault.example.com"), On(), m: mcp).ShouldContain(p => p.Contains("Mcp:ReadOnly"));
    }

    [Fact]
    public void A_hidden_download_tool_refuses_the_route()
    {
        var mcp = EdgeFronted();
        mcp.DisabledTools = ["VAULT_DOWNLOAD_PREPARE"];
        Validate(Origin("https://vault.example.com"), On(), m: mcp).ShouldContain(p => p.Contains("Mcp:DisabledTools"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("vault.example.com")]
    [InlineData("http://vault.example.com")]
    [InlineData("https://user:pw@vault.example.com")]
    [InlineData("https://vault.example.com/transfer")]
    [InlineData("https://vault.example.com/?q=1")]
    [InlineData("https://elsewhere.example.com")]
    public void An_unusable_public_origin_is_refused(string origin) =>
        Validate(Origin(origin), On(), m: EdgeFronted()).ShouldNotBeEmpty();

    [Fact]
    public void Local_only_refuses_an_access_deployment() =>
        Validate(Origin("http://127.0.0.1:3535", localOnly: true), m: EdgeFronted())
            .ShouldContain(p => p.Contains("Mcp:Access:Enabled false"));

    [Fact]
    public void Local_only_refuses_a_public_allowed_host() =>
        Validate(Origin("http://127.0.0.1:3535", localOnly: true),
                m: new McpOptions { AllowedHosts = ["vault.example.com"] })
            .ShouldContain(p => p.Contains("non-loopback host"));

    [Fact]
    public void Local_only_refuses_the_fake_edge_flag() =>
        Validate(Origin("http://127.0.0.1:3535", localOnly: true),
                m: new McpOptions { Access = new AccessOptions { AllowPublicHostsWithoutAccess = true } })
            .ShouldContain(p => p.Contains("AllowPublicHostsWithoutAccess"));

    [Fact]
    public void Local_only_requires_a_loopback_origin() =>
        Validate(Origin("http://vault.example.com", localOnly: true), On())
            .ShouldContain(p => p.Contains("loopback Transfer:PublicOrigin"));

    [Fact]
    public void Limits_must_be_coherent()
    {
        var origin = Origin("https://vault.example.com");
        Validate(origin, new DownloadOptions { Enabled = true, MaxActiveGrants = 4, MaxActiveGrantsPerPrincipal = 5 }, m: EdgeFronted())
            .ShouldContain(p => p.Contains("MaxActiveGrantsPerPrincipal"));
        Validate(new TransferOptions { PublicOrigin = "https://vault.example.com", MaxBufferedBytes = SyncMax - 1 }, On(), m: EdgeFronted())
            .ShouldContain(p => p.Contains("MaxBufferedBytes"));
        Validate(new TransferOptions { PublicOrigin = "https://vault.example.com", MaxTrackedGrants = 2 }, On(), m: EdgeFronted())
            .ShouldContain(p => p.Contains("MaxTrackedGrants"));
        Validate(origin, new DownloadOptions { Enabled = true, GrantLifetimeSeconds = 0 }, m: EdgeFronted())
            .ShouldContain(p => p.Contains("GrantLifetimeSeconds"));
    }

    [Fact]
    public void A_malformed_allowed_prefix_is_refused() =>
        Validate(Origin("https://vault.example.com"), new DownloadOptions { Enabled = true, AllowedPrefixes = ["../etc"] },
                m: EdgeFronted())
            .ShouldContain(p => p.Contains("Download:AllowedPrefixes"));

    [Fact]
    public void Principals_come_only_from_validated_claims()
    {
        TransferPrincipal.FromClaims("https://t", "u1", "cn").ShouldBe(new TransferPrincipal("https://t", "sub", "u1"));
        TransferPrincipal.FromClaims("https://t", "", "svc").ShouldBe(new TransferPrincipal("https://t", "service", "svc"));
        TransferPrincipal.FromClaims("https://t", null, null).ShouldBeNull();
        TransferPrincipal.FromClaims(null, "u1", null).ShouldBeNull();
        // Structured equality: no joined string can make a user collide with a service.
        TransferPrincipal.FromClaims("https://t", "x", null)
            .ShouldNotBe(TransferPrincipal.FromClaims("https://t", "", "x"));
    }
}
