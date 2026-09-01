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
///
/// A cursor position must be a TOTAL order over the records it paginates,
/// because the resume filter is "strictly after this position" — two records
/// sharing one position means the second is silently dropped the moment a
/// page boundary falls between them, on a response still claiming
/// <c>truncated: false</c> at the end. (path, line, column) is total for
/// search: rg emits one record per submatch and submatches on a line have
/// distinct columns. It is NOT total for LINT, where one wikilink can be
/// both an unescaped pipe in a table row and an unresolved target — two
/// findings, one position. Hence the optional KEY: a fourth component that
/// makes the order total again, carried in the cursor and compared after the
/// column. Only lint sets it, but any future surface emitting more than one
/// record per position needs it too.
/// </summary>
internal static class QueryCursor
{
    private sealed record Payload(string F, string P, int L, int C, string? K = null);

    internal static string Encode(
        string fingerprint, string lastPath, int lastLine = 0, int lastColumn = 0, string? lastKey = null)
    {
        var json = JsonSerializer.Serialize(new Payload(fingerprint, lastPath, lastLine, lastColumn, lastKey));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Far above any real cursor (fingerprint + path + ints); a bound before base64 decode.</summary>
    private const int MaxCursorLength = 4096;

    /// <summary>The keyless projection, for the surfaces whose position is already total.</summary>
    internal static (string Path, int Line, int Column) Decode(string cursor, string expectedFingerprint)
    {
        var (path, line, column, _) = DecodeKeyed(cursor, expectedFingerprint);
        return (path, line, column);
    }

    /// <summary>
    /// ONE parser behind both projections. A cursor issued before the key
    /// existed decodes with an EMPTY key, so the record it points at compares
    /// as still-to-come and is emitted a second time. That is the deliberate
    /// direction: a visible duplicate finding, never a silent omission.
    /// </summary>
    internal static (string Path, int Line, int Column, string Key) DecodeKeyed(
        string cursor, string expectedFingerprint)
    {
        if (cursor.Length > MaxCursorLength)
            throw new KnapperException(VaultErrorCode.InvalidCursor,
                $"cursor is implausibly long ({cursor.Length} chars; cap {MaxCursorLength})");
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
        return (payload.P, payload.L, payload.C, payload.K ?? "");
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

    /// <summary>
    /// The same order with the tiebreaking key appended, for a surface that
    /// can emit more than one record at a position. The key is compared
    /// ORDINALLY, and the emitting service must SORT by it too — a
    /// comparison the emission order disagrees with reintroduces exactly the
    /// omission the key exists to remove.
    /// </summary>
    internal static int ComparePosition(
        (string Path, int Line, int Column, string Key) a, (string Path, int Line, int Column, string Key) b)
    {
        var byPosition = ComparePosition((a.Path, a.Line, a.Column), (b.Path, b.Line, b.Column));
        return byPosition != 0 ? byPosition : string.CompareOrdinal(a.Key, b.Key);
    }
}
