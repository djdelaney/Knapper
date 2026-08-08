using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Knapper.Mcp;

/// <summary>
/// Reads Cloudflare Access's signing keys from <c>/cdn-cgi/access/certs</c>
/// (a bare JWKS — Access publishes no OIDC discovery document) and presents
/// them as the configuration object ConfigurationManager expects, keeping its
/// caching, bounded refresh, and negative-result backoff. Throws rather than
/// returning a keyless configuration: at boot that refuses startup loudly; on
/// refresh it keeps the last good key set instead of degrading to
/// "authenticates nobody".
/// </summary>
internal sealed class AccessCertsRetriever(string issuer)
    : IConfigurationRetriever<OpenIdConnectConfiguration>
{
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(
        string address, IDocumentRetriever retriever, CancellationToken cancel)
    {
        var json = await retriever.GetDocumentAsync(address, cancel).ConfigureAwait(false);
        var configuration = new OpenIdConnectConfiguration { Issuer = issuer, JwksUri = address };
        foreach (var key in new JsonWebKeySet(json).GetSigningKeys())
            configuration.SigningKeys.Add(key);
        if (configuration.SigningKeys.Count == 0)
        {
            throw new InvalidOperationException(
                $"Cloudflare Access returned no usable signing keys from '{address}'. " +
                "Check that Mcp:Access:TeamDomain names your Zero Trust team domain.");
        }
        return configuration;
    }
}
