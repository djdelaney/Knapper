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

    /// <summary>
    /// Streamed, for files past the read cap: the hash is the precondition
    /// for move/soft-delete, so it must exist even when the BODY can't be
    /// returned — a capped stat would make large synced attachments
    /// unmanageable through the authoritative interface.
    /// </summary>
    public static string Sha256HexOfFile(string absolutePath)
    {
        using var stream = File.OpenRead(absolutePath);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static bool Matches(string expected, ReadOnlySpan<byte> data) =>
        string.Equals(expected.Trim(), Sha256Hex(data), StringComparison.OrdinalIgnoreCase);
}
