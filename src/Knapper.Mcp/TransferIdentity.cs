using Knapper.Core.Options;
using Knapper.Core.Transfer;
using Microsoft.Extensions.Options;

namespace Knapper.Mcp;

/// <summary>
/// The verified principal of the current MCP request
/// (docs/proposals/transfer-grants.md §8). Read per request from the
/// authenticated HttpContext — never cached on a singleton, since every
/// concurrent session shares this object.
///
/// Fails closed: Access's loopback exemption (<c>Access:AllowLoopback</c>)
/// admits a same-box caller with NO claims, and that admission supplies no
/// principal — cloudflared delivers internet traffic from a loopback peer,
/// which is exactly the confusion HostGuard exists to prevent. Only
/// <c>Transfer:LocalOnly</c>, which startup permits solely on a
/// configuration that already means "this machine only", mints the local key.
/// </summary>
public sealed class TransferIdentity(
    IHttpContextAccessor http,
    IOptions<McpOptions> mcp,
    IOptions<TransferOptions> transfer)
{
    public TransferPrincipal? Current()
    {
        var context = http.HttpContext;
        if (transfer.Value.LocalOnly)
        {
            return !mcp.Value.Access.Enabled && HostGuard.IsLocalRequest(context)
                ? TransferPrincipal.LocalOwner
                : null;
        }
        if (!mcp.Value.Access.Enabled)
            return null;
        var user = context?.User;
        if (user?.Identity?.IsAuthenticated != true)
            return null;
        return TransferPrincipal.FromClaims(
            user.FindFirst("iss")?.Value,
            user.FindFirst("sub")?.Value,
            user.FindFirst("common_name")?.Value);
    }
}
