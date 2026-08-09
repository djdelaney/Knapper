using System.Text;
using System.Text.Json;
using Knapper.Core.Vault;

namespace Knapper.Core.Query;

/// <summary>
/// Opaque continuation cursors. A cursor embeds a fingerprint of the query's
/// filter fields; presenting it to a different query is a typed
/// <see cref="VaultErrorCode.InvalidCursor"/> — pages from mismatched
/// queries would omit or duplicate records, which the completeness contract
/// forbids. Position is (path, line, column); list-shaped queries use path
/// only.
/// </summary>
internal static class QueryCursor
{
    private sealed record Payload(string F, string P, int L, int C);

    internal static string Encode(string fingerprint, string lastPath, int lastLine = 0, int lastColumn = 0)
    {
        var json = JsonSerializer.Serialize(new Payload(fingerprint, lastPath, lastLine, lastColumn));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    internal static (string Path, int Line, int Column) Decode(string cursor, string expectedFingerprint)
    {
        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(
                Encoding.UTF8.GetString(Convert.FromBase64String(cursor)));
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException)
        {
            throw new KnapperException(VaultErrorCode.InvalidCursor, "cursor is not parseable");
        }
        if (payload is null || payload.P is null || payload.F != expectedFingerprint)
        {
            throw new KnapperException(VaultErrorCode.InvalidCursor,
                "cursor does not belong to this query — pass the same filters that produced it");
        }
        return (payload.P, payload.L, payload.C);
    }

    /// <summary>Fingerprint of a query's filter fields (unit-separated, hashed).</summary>
    internal static string Fingerprint(params object?[] parts) =>
        VaultHash.Sha256Hex(Encoding.UTF8.GetBytes(
            string.Join('\x1f', parts.Select(p => p switch
            {
                null => "\x00",
                System.Collections.IEnumerable e and not string =>
                    string.Join('\x1e', e.Cast<object?>()),
                _ => p.ToString(),
            }))));

    /// <summary>
    /// THE path order of the whole query surface: raw UTF-8 byte order,
    /// because that is what rg's <c>--sort=path</c> emits. It is NOT
    /// <c>string.CompareOrdinal</c> (UTF-16 code units): the two diverge
    /// exactly when a non-BMP name (emoji — common in Obsidian) meets
    /// U+E000..U+FFFF — surrogates sort low in UTF-16 while their UTF-8
    /// encoding sorts high. A cursor compared in the wrong order silently
    /// skips records on every later page while the final page still claims
    /// <c>truncated: false</c>. Every sort and cursor filter over paths
    /// (search stream, lister, frontmatter) must use this method.
    /// </summary>
    internal static int ComparePathUtf8(string a, string b) =>
        Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b));

    /// <summary>Order matches stream in: path (UTF-8 bytes, like rg), then line, then column.</summary>
    internal static int ComparePosition(
        (string Path, int Line, int Column) a, (string Path, int Line, int Column) b)
    {
        var byPath = ComparePathUtf8(a.Path, b.Path);
        if (byPath != 0)
            return byPath;
        var byLine = a.Line.CompareTo(b.Line);
        return byLine != 0 ? byLine : a.Column.CompareTo(b.Column);
    }
}
