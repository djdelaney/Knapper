using System.Security.Cryptography;

namespace Knapper.Core.Vault;

/// <summary>
/// SHA-256 as lowercase hex — the mutation precondition currency of the whole
/// service. Comparisons are case-insensitive on input (clients echo hashes
/// back), output is always lowercase.
/// </summary>
public static class VaultHash
{
    public static string Sha256Hex(ReadOnlySpan<byte> data) =>
        Convert.ToHexStringLower(SHA256.HashData(data));

    public static bool Matches(string expected, ReadOnlySpan<byte> data) =>
        string.Equals(expected.Trim(), Sha256Hex(data), StringComparison.OrdinalIgnoreCase);
}
