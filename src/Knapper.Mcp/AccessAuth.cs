using Knapper.Core.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Knapper.Mcp;

/// <summary>
/// Validates Cloudflare Access's <c>Cf-Access-Jwt-Assertion</c> at the origin
/// (ported from Mailvec's proven implementation). Everything reads its
/// configuration lazily from DI, so security controls always see the resolved
/// options — never a builder-time snapshot that env vars haven't reached.
/// Registered unconditionally and inert: with Access disabled no middleware
/// is wired and no policy lands on any endpoint.
/// </summary>
internal static class AccessAuth
{
    /// <summary>
    /// The header Access adds to requests it admits. NOT <c>Authorization</c> —
    /// on a claude.ai connector request that carries the connector's own OAuth
    /// token, a different credential with a different issuer. The fallback to
    /// it is explicitly suppressed below.
    /// </summary>
    internal const string AssertionHeader = "Cf-Access-Jwt-Assertion";

    /// <summary>Everything vault-bearing: the MCP endpoint and /health.</summary>
    internal const string OwnerPolicy = "knapper-access-owner";

    /// <summary>/up only — additionally admits the path-scoped monitoring app's audience.</summary>
    internal const string MonitoringPolicy = "knapper-access-monitoring";

    internal static void AddAccessAuthentication(IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<McpOptions>>((options, mcp) =>
            {
                var access = mcp.Value.Access;
                if (!access.Enabled)
                {
                    options.RequireHttpsMetadata = false;
                    return;
                }

                // A bare JWKS wrapped for ConfigurationManager — Access
                // publishes no OIDC discovery document; MetadataAddress would
                // 404 invisibly and authenticate nobody. See AccessCertsRetriever.
                options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                    access.CertsAddress,
                    new AccessCertsRetriever(access.TeamDomain.TrimEnd('/')),
                    new HttpDocumentRetriever { RequireHttps = true });
                options.RequireHttpsMetadata = true;
                options.MapInboundClaims = false; // keep "aud"/"email" as minted

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = access.TeamDomain.TrimEnd('/'),
                    ValidateAudience = true,
                    ValidAudiences = access.AllAudiences(),
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                    // Assertions are minted seconds before use by an edge whose
                    // clock is not ours to doubt; a long skew only extends
                    // replayed-token life.
                    ClockSkew = TimeSpan.FromSeconds(60),
                };

                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        var assertion = ctx.Request.Headers[AssertionHeader].ToString();
                        if (string.IsNullOrWhiteSpace(assertion))
                        {
                            // NoResult, not "leave Token null": a null token
                            // makes the handler fall back to Authorization,
                            // which holds a DIFFERENT credential here.
                            ctx.NoResult();
                            return Task.CompletedTask;
                        }
                        ctx.Token = assertion;
                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(OwnerPolicy, p =>
                p.AddRequirements(new AccessAudienceRequirement(AccessScope.Owner)));
            options.AddPolicy(MonitoringPolicy, p =>
                p.AddRequirements(new AccessAudienceRequirement(AccessScope.Monitoring)));
        });
        services.AddSingleton<IAuthorizationHandler, AccessAudienceHandler>();
    }

    /// <summary>
    /// Fetch the signing keys at startup, or refuse to start. Lazily-fetched
    /// keys fail into an EventSource no logger hears, leaving a server that
    /// logs "ENABLED", passes its loopback healthcheck, and 401s every real
    /// caller — a silent total outage Mailvec actually shipped once. Bounded:
    /// a hang at boot is worse than a refusal.
    /// </summary>
    internal static async Task VerifySigningKeysAsync(
        IServiceProvider services, ILogger logger, CancellationToken ct = default)
    {
        var access = services.GetRequiredService<IOptions<McpOptions>>().Value.Access;
        var options = services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        if (options.ConfigurationManager is not BaseConfigurationManager manager)
        {
            throw new InvalidOperationException(
                "Mcp:Access:Enabled is true but no signing-key source is configured — every request would fail validation.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        BaseConfiguration configuration;
        try
        {
            configuration = await manager.GetBaseConfigurationAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Could not retrieve the Cloudflare Access signing keys from '{access.CertsAddress}'. " +
                "Knapper would authenticate nobody, so it will not start. Check Mcp:Access:TeamDomain " +
                "and that the origin can reach your Zero Trust team domain.", ex);
        }
        if (configuration.SigningKeys.Count == 0)
        {
            throw new InvalidOperationException(
                $"Cloudflare Access returned no signing keys from '{access.CertsAddress}' — refusing to start.");
        }
        logger.LogInformation(
            "Cloudflare Access signing keys loaded from {CertsUrl}: {KeyCount} key(s), kid {KeyIds}.",
            access.CertsAddress, configuration.SigningKeys.Count,
            string.Join(", ", configuration.SigningKeys.Select(k => k.KeyId)));
    }
}

internal enum AccessScope
{
    Owner,
    Monitoring,
}

/// <summary>
/// The scheme asks "is this a token for this deployment"; this policy asks
/// "for THIS endpoint" — the split is what keeps a leaked monitoring
/// credential away from the vault.
/// </summary>
internal sealed class AccessAudienceRequirement(AccessScope scope) : IAuthorizationRequirement
{
    public AccessScope Scope { get; } = scope;
}

internal sealed class AccessAudienceHandler(
    IHttpContextAccessor http,
    IOptions<McpOptions> mcp,
    ILogger<AccessAudienceHandler> logger)
    : AuthorizationHandler<AccessAudienceRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, AccessAudienceRequirement requirement)
    {
        var access = mcp.Value.Access;

        // Loopback PEER is not enough: cloudflared proxies every tunneled
        // internet request to this port from 127.0.0.1, so exempting on the
        // peer alone would silently disable origin validation for the whole
        // public surface. A genuine same-box caller also asks for a loopback
        // Host; a tunneled request carries the public hostname.
        if (access.AllowLoopback && HostGuard.IsLocalRequest(http.HttpContext))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (context.User.Identity?.IsAuthenticated == true)
        {
            var permitted = requirement.Scope == AccessScope.Monitoring
                ? access.MonitoringAudiences()
                : access.OwnerAudiences();
            if (context.User.FindAll("aud").Select(c => c.Value)
                .Any(a => permitted.Contains(a, StringComparer.Ordinal)))
            {
                context.Succeed(requirement);
                return Task.CompletedTask;
            }
            // A VALID token for a different app reaching for the vault — the
            // denial worth a log line. No token contents, no vault content.
            logger.LogWarning(
                "Access assertion rejected for {Path}: audience not permitted on this endpoint.",
                http.HttpContext?.Request.Path.Value);
        }
        // Never Fail() — declining is already deny-by-default, and Fail() would
        // veto any future composed policy.
        return Task.CompletedTask;
    }
}
