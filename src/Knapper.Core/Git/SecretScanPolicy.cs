using System.Collections.Frozen;
using System.Text;

namespace Knapper.Core.Git;

/// <summary>
/// WHICH bytes the secret scan reads, and HOW it reads them — the ONE
/// definition, shared by the committer (<see cref="GitCommitJob"/>, over
/// staged blobs) and the upload commit's pre-landing scan
/// (docs/proposals/transfer-grants.md §4.3, over a payload before it lands).
///
/// The two must agree exactly, and the direction that matters is the
/// committer's: one credential-shaped string in a staged file refuses EVERY
/// vault commit until a human edits it, and the commit job retries each
/// tick. A pre-landing rule narrower than the committer's admits precisely
/// the files that then wedge it — a Latin-1 <c>.env</c> is not strict UTF-8
/// but decodes to a match here; a stored zip holding a key is "binary" but is
/// under the size cap. So nothing may decide scan coverage or decoding
/// anywhere else; a differential test runs both callers over the same
/// payloads and demands the same verdict. The other half of parity is that
/// staging must not change the bytes: the committer refuses any staged path
/// with a content-converting git attribute
/// (<see cref="GitCommitJob.ContentConvertingAttributes"/>), so the bytes
/// <see cref="Scan"/> judges are the bytes that enter history.
/// </summary>
public static class SecretScanPolicy
{
    /// <summary>
    /// Files above this are not text-scanned unless their extension is in
    /// <see cref="AlwaysScannedExtensions"/>. The scanner is a tripwire for
    /// credential-shaped TEXT; anything this large is attachment-class (Sync
    /// deliberately carries image/audio/video/pdf — brief §5), and decoding it
    /// would materialize every synced photo into a string on every commit
    /// run. A DOCUMENTED limitation like the scanner's line-based matching.
    /// </summary>
    public const long MaxScanBytes = 4_000_000;

    /// <summary>
    /// Text formats scanned at ANY size: the cap above exists for
    /// attachment-class files, and applying it to notes left every .md
    /// between the cap and Sync's 5 MiB ceiling unscanned. The extension only
    /// CLASSIFIES; the committer still fetches bytes by blob SHA, never by name.
    /// </summary>
    public static readonly FrozenSet<string> AlwaysScannedExtensions = FrozenSet.ToFrozenSet(
        [
            ".md", ".txt", ".json", ".canvas", ".yaml", ".yml", ".csv", ".env", ".ini", ".conf",
            ".toml", ".xml", ".html", ".js", ".ts", ".py", ".sh", ".ps1", ".sql", ".log",
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a file at <paramref name="path"/> of <paramref name="size"/> bytes is scanned.</summary>
    public static bool ShouldScan(string path, long size) =>
        size <= MaxScanBytes || AlwaysScannedExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// The text the scanner sees for these bytes. UTF-8, LOSSY — invalid
    /// sequences become U+FFFD and the ASCII runs around them still match, so
    /// binaries and Latin-1 text ARE scanned — with a byte-order mark honoured
    /// (UTF-8, UTF-16 LE/BE, UTF-32), so a UTF-16 text file is read as text
    /// rather than as NUL-interleaved bytes.
    ///
    /// When the mark names a NON-UTF-8 encoding, the plain lossy-UTF-8 view of
    /// the same bytes is appended as well. Two bytes are all a mark is: any
    /// file that merely STARTS with <c>FF FE</c> or <c>FE FF</c> — a binary, or
    /// ASCII with a stray prefix — was decoded wholesale as UTF-16 and every
    /// credential in it went unseen, by the committer included (found
    /// 2026-11-08 when the differential test was made to pin known answers
    /// instead of mere agreement). Scanning both readings costs a second
    /// decode of a file at most <see cref="MaxScanBytes"/> long, or a text
    /// file Sync caps at 5 MiB; a finding from the appended view reports a
    /// line number past the first view's end, which is acceptable for a
    /// masked tripwire.
    /// </summary>
    public static string Decode(byte[] bytes)
    {
        var lossyUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        using var reader = new StreamReader(
            new MemoryStream(bytes, writable: false), lossyUtf8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        return reader.CurrentEncoding is UTF8Encoding
            ? text
            : text + "\n" + lossyUtf8.GetString(bytes);
    }

    /// <summary>
    /// The pre-landing verdict for a payload headed to <paramref name="path"/>:
    /// the findings the committer would raise if these bytes were staged
    /// there, or none when the committer would not scan them.
    /// </summary>
    public static IReadOnlyList<SecretScanner.Finding> Scan(string path, byte[] bytes) =>
        ShouldScan(path, bytes.LongLength) ? SecretScanner.Scan(path, Decode(bytes)) : [];
}
