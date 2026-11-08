namespace Knapper.Core.Transfer;

/// <summary>
/// Who a grant belongs to (docs/proposals/transfer-grants.md §8.1): derived
/// from VALIDATED Access claims, compared as a structured value — never a
/// string joined with an ambiguous delimiter, never email, never clientInfo
/// or any header the client supplies. The display-oriented audit client
/// string is attribution, never this key.
/// </summary>
public sealed record TransferPrincipal(string Issuer, string Kind, string Subject)
{
    /// <summary>The fixed key Transfer:LocalOnly mints for a same-machine caller (§8.3).</summary>
    public static readonly TransferPrincipal LocalOwner = new("local", "loopback", "owner");

    /// <summary>
    /// The key for a validated Access identity: <c>(issuer, "sub", subject)</c>
    /// for a user, <c>(issuer, "service", commonName)</c> for an owner service
    /// assertion with no subject. Null — fail closed — when the issuer is
    /// missing or neither identifier is present.
    /// </summary>
    public static TransferPrincipal? FromClaims(string? issuer, string? subject, string? commonName)
    {
        if (string.IsNullOrWhiteSpace(issuer))
            return null;
        if (!string.IsNullOrWhiteSpace(subject))
            return new TransferPrincipal(issuer, "sub", subject);
        if (!string.IsNullOrWhiteSpace(commonName))
            return new TransferPrincipal(issuer, "service", commonName);
        return null;
    }

    /// <summary>For audit lines only. Each part is bounded and the separator cannot be forged into a key.</summary>
    public string ForAudit() => $"{Kind}:{Bound(Subject)}@{Bound(Issuer)}";

    private static string Bound(string s) => s.Length <= 128 ? s : s[..128];
}
