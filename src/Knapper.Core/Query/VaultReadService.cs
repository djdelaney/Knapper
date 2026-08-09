using System.Text;
using Knapper.Core.Generation;
using Knapper.Core.Options;
using Knapper.Core.Vault;

namespace Knapper.Core.Query;

/// <summary>
/// vault_read / vault_batch_read / vault_stat (brief §6). Reads always
/// return the SHA-256 of the WHOLE file's raw bytes — even ranged reads —
/// because that hash is the currency of the mutation precondition. Files
/// beyond the read cap fail TooLarge explicitly; a silently truncated
/// "complete" file is never returned. Text operations are UTF-8-strict
/// (a UTF-8 BOM is recognized and stripped from content, reported as
/// encoding "utf-8-bom"); anything else is typed NotUtf8.
/// </summary>
public sealed class VaultReadService(
    VaultPathResolver resolver, VaultOptions options, VaultGenerationCounter generation)
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    public VaultReadResult Read(string path, int? startLine = null, int? endLine = null)
    {
        var generationStart = generation.Current;
        var vp = resolver.Resolve(path);
        var bytes = ReadBytesChecked(vp);
        var (content, encoding) = DecodeStrict(bytes, vp.Relative);
        var lines = SplitLines(content);

        int? rangeStart = null, rangeEnd = null;
        if (startLine is not null || endLine is not null)
        {
            var start = startLine ?? 1;
            var end = endLine ?? lines.Count;
            if (start < 1 || end < start)
                throw new KnapperException(VaultErrorCode.InvalidArgument,
                    $"invalid line range [{start}, {end}] — lines are 1-based and end must be >= start");
            if (start > lines.Count)
                throw new KnapperException(VaultErrorCode.InvalidArgument,
                    $"range starts at line {start} but the file has only {lines.Count} lines");
            // End is clamped, explicitly: the echoed RangeEnd reports what was returned.
            end = Math.Min(end, lines.Count);
            content = string.Join('\n', lines.Skip(start - 1).Take(end - start + 1));
            rangeStart = start;
            rangeEnd = end;
        }

        var info = new FileInfo(vp.Absolute);
        var generationEnd = generation.Current;
        return new VaultReadResult(
            vp.Relative,
            content,
            VaultHash.Sha256Hex(bytes),
            bytes.Length,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            encoding,
            lines.Count,
            rangeStart,
            rangeEnd,
            generationStart,
            generationEnd,
            generationEnd != generationStart);
    }

    /// <summary>
    /// Per-path results; one bad file never hides the others' (brief §6).
    /// Only a malformed request as a whole (too many items) fails outright.
    /// </summary>
    public VaultBatchReadResult BatchRead(
        IReadOnlyList<VaultReadRequest> requests, CancellationToken ct = default)
    {
        if (requests.Count == 0)
            throw new KnapperException(VaultErrorCode.InvalidArgument, "batch is empty");
        if (requests.Count > options.MaxBatchItems)
        {
            throw new KnapperException(VaultErrorCode.InvalidArgument,
                $"batch has {requests.Count} items; the cap is {options.MaxBatchItems} — split the request");
        }
        var generationStart = generation.Current;
        var results = new List<VaultBatchReadItem>(requests.Count);
        foreach (var request in requests)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                results.Add(new VaultBatchReadItem(
                    request.Path, Read(request.Path, request.StartLine, request.EndLine), null, null));
            }
            catch (KnapperException e)
            {
                results.Add(new VaultBatchReadItem(request.Path, null, e.Code, e.Message));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // One transiently-unreadable file (Sync mid-replace, disk
                // hiccup) must never hide the other items' results. The
                // caller gets the RELATIVE path only — OS messages embed the
                // absolute vault prefix.
                results.Add(new VaultBatchReadItem(request.Path, null, VaultErrorCode.IoError,
                    $"filesystem failure reading {request.Path} — transient or environmental; retry"));
            }
        }
        var generationEnd = generation.Current;
        return new VaultBatchReadResult(results, generationStart, generationEnd, generationEnd != generationStart);
    }

    public VaultStatResult Stat(string path)
    {
        var generationStart = generation.Current;
        var vp = resolver.Resolve(path);
        if (Directory.Exists(vp.Absolute))
        {
            var dirInfo = new DirectoryInfo(vp.Absolute);
            return Finish(new VaultStatResult(vp.Relative, true, true, null,
                new DateTimeOffset(dirInfo.LastWriteTimeUtc, TimeSpan.Zero), null, null, null, null, 0, 0, false));
        }
        if (!File.Exists(vp.Absolute))
            return Finish(new VaultStatResult(vp.Relative, false, false, null, null, null, null, null, null, 0, 0, false));

        var info = new FileInfo(vp.Absolute);
        var mtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        if (info.Length > options.MaxReadBytes)
        {
            // The cap bounds the BODY, never the hash: the SHA is the
            // precondition currency for move/soft-delete, and a stat that
            // omitted it would strand every large synced attachment.
            // Text/encoding detection is bounded to a prefix; totalLines
            // stays null (it would require a full decode).
            var (largeEncoding, largeIsText) = DetectTextBounded(vp.Absolute);
            return Finish(new VaultStatResult(
                vp.Relative, true, false, info.Length, mtime,
                largeEncoding, largeIsText, VaultHash.Sha256HexOfFile(vp.Absolute), null, 0, 0, false));
        }
        var bytes = File.ReadAllBytes(vp.Absolute);
        string? encoding;
        bool isText;
        int? totalLines = null;
        try
        {
            var (content, enc) = DecodeStrict(bytes, vp.Relative);
            encoding = enc;
            isText = true;
            totalLines = SplitLines(content).Count;
        }
        catch (KnapperException e) when (e.Code == VaultErrorCode.NotUtf8)
        {
            encoding = "binary";
            isText = false;
        }
        return Finish(new VaultStatResult(
            vp.Relative, true, false, bytes.Length, mtime,
            encoding, isText, VaultHash.Sha256Hex(bytes), totalLines, 0, 0, false));

        // One exit point stamps the span so no return path can forget it.
        VaultStatResult Finish(VaultStatResult r)
        {
            var generationEnd = generation.Current;
            return r with
            {
                GenerationStart = generationStart,
                GenerationEnd = generationEnd,
                ChangedDuringRead = generationEnd != generationStart,
            };
        }
    }

    // ---- shared helpers (frontmatter search reuses these) --------------

    internal byte[] ReadBytesChecked(VaultPath vp)
    {
        if (Directory.Exists(vp.Absolute))
            throw new KnapperException(VaultErrorCode.InvalidArgument, $"path is a directory: {vp.Relative}");
        FileInfo info = new(vp.Absolute);
        if (!info.Exists)
            throw new KnapperException(VaultErrorCode.NotFound, $"no such file: {vp.Relative}");
        if (info.Length > options.MaxReadBytes)
        {
            throw new KnapperException(VaultErrorCode.TooLarge,
                $"{vp.Relative} is {info.Length} bytes; the read cap is {options.MaxReadBytes}. " +
                "This vault's notes should never approach the cap — if this file is legitimate, raise Vault:MaxReadBytes.");
        }
        // Bounded read, not ReadAllBytes: an external writer can grow the
        // file between the stat above and the read (TOCTOU) — the buffer is
        // sized from the stat, and one extra probe byte detects growth
        // instead of materializing it.
        using var stream = File.OpenRead(vp.Absolute);
        var buffer = new byte[info.Length + 1];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        if (read == buffer.Length)
        {
            throw new KnapperException(VaultErrorCode.IoError,
                $"{vp.Relative} grew while being read (external writer) — re-run the read");
        }
        return buffer[..read];
    }

    /// <summary>
    /// Bounded text/encoding detection for files past the read cap: strict
    /// UTF-8 validation of the first 64 KiB only (a later invalid byte goes
    /// undetected — documented bound, the alternative is an uncapped read).
    /// A multi-byte sequence the cut may have truncated is trimmed before
    /// validating so a clean boundary can't misread as binary.
    /// </summary>
    private static (string Encoding, bool IsText) DetectTextBounded(string absolutePath)
    {
        const int PrefixBytes = 64 * 1024;
        using var stream = File.OpenRead(absolutePath);
        var buffer = new byte[PrefixBytes];
        var read = stream.ReadAtLeast(buffer, PrefixBytes, throwOnEndOfStream: false);
        var hasBom = read >= 3 && buffer.AsSpan(0, 3).SequenceEqual(Utf8Bom);

        var end = read;
        for (var back = 0; back < 4 && end > 0; back++)
        {
            var b = buffer[end - 1];
            if (b < 0x80)
                break; // ASCII tail — nothing dangling
            end--;
            if (b >= 0xC0)
                break; // removed the dangling sequence's lead byte
        }
        try
        {
            _ = StrictUtf8.GetString(buffer.AsSpan(hasBom ? 3 : 0, end - (hasBom ? 3 : 0)));
            return (hasBom ? "utf-8-bom" : "utf-8", true);
        }
        catch (DecoderFallbackException)
        {
            return ("binary", false);
        }
    }

    internal static (string Content, string Encoding) DecodeStrict(byte[] bytes, string relativeForError)
    {
        var hasBom = bytes.Length >= 3 && bytes.AsSpan(0, 3).SequenceEqual(Utf8Bom);
        try
        {
            var content = StrictUtf8.GetString(hasBom ? bytes.AsSpan(3) : bytes);
            return (content, hasBom ? "utf-8-bom" : "utf-8");
        }
        catch (DecoderFallbackException)
        {
            throw new KnapperException(VaultErrorCode.NotUtf8,
                $"{relativeForError} is not valid UTF-8 text — text operations refuse it (use vault_stat for metadata)");
        }
    }

    /// <summary>
    /// Lines split on '\n' ('\r' preserved — content is raw). A trailing
    /// newline does not create a phantom empty last line: "a\n" is one line.
    /// </summary>
    internal static IReadOnlyList<string> SplitLines(string content)
    {
        if (content.Length == 0)
            return [];
        var parts = content.Split('\n');
        return content.EndsWith('\n') ? parts[..^1] : parts;
    }
}
