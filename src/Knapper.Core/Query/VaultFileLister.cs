using Knapper.Core.Generation;
using Knapper.Core.Options;
using Knapper.Core.Vault;

namespace Knapper.Core.Query;

/// <summary>
/// vault_files (brief §6): the constrained find/`rg --files` equivalent.
/// Native walk (mtime/size filters need stat anyway), deterministic ordinal
/// sort of the full relative path, cursor pagination, hidden entries
/// invisible (dotfiles at any depth — the same rule ripgrep applies during
/// searches, so the two surfaces can never disagree about what exists).
/// Symlinks are skipped, never followed.
///
/// <para>The walk is collect-then-sort (the vault is ~230 files by design);
/// if the time budget dies mid-walk there is no defensible partial order,
/// so that is a typed QueryTimeout rather than a page that later omits or
/// duplicates records.</para>
/// </summary>
public sealed class VaultFileLister(
    VaultPathResolver resolver,
    VaultGenerationCounter generation,
    VaultOptions options)
{
    public QueryEnvelope<VaultFileEntry> List(VaultFilesQuery query, CancellationToken ct = default)
    {
        var generationStart = generation.Current;
        var (rootAbsolute, prefixRelative) = ResolvePrefix(query.PathPrefix);
        var glob = query.Glob is null ? null : Globbing.Translate(query.Glob);
        var extensions = NormalizeExtensions(query.Extensions);
        var sizeFiltered = query.MinSize is not null || query.MaxSize is not null;

        var fingerprint = QueryCursor.Fingerprint(
            "files", prefixRelative, query.Glob, query.Extensions, query.Kind,
            query.MtimeAfter, query.MtimeBefore, query.MinSize, query.MaxSize, query.IncludeSha);
        string? cursorPath = query.Cursor is null
            ? null
            : QueryCursor.Decode(query.Cursor, fingerprint).Path;
        var pageSize = Math.Clamp(query.MaxResults ?? options.MaxResultsPerPage, 1, options.MaxResultsPerPage);

        var deadline = Environment.TickCount64 + options.QueryTimeoutMs;
        var scannedFiles = 0;
        var matched = new List<(string Relative, FileSystemInfo Info)>();

        foreach (var (relative, info) in Walk(new DirectoryInfo(rootAbsolute), prefixRelative, ct, deadline))
        {
            var isDirectory = info is DirectoryInfo;
            if (!isDirectory)
                scannedFiles++;

            if (query.Kind == EntryKind.File && isDirectory)
                continue;
            if (query.Kind == EntryKind.Directory && !isDirectory)
                continue;
            if (sizeFiltered && isDirectory)
                continue;
            if (glob is not null && !Globbing.IsMatch(glob, relative))
                continue;
            if (extensions.Count > 0 &&
                (isDirectory || !extensions.Contains(ExtensionOf(info.Name))))
                continue;
            var mtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            if (query.MtimeAfter is { } after && mtime <= after)
                continue;
            if (query.MtimeBefore is { } before && mtime >= before)
                continue;
            if (!isDirectory)
            {
                var size = ((FileInfo)info).Length;
                if (query.MinSize is { } min && size < min)
                    continue;
                if (query.MaxSize is { } max && size > max)
                    continue;
            }
            matched.Add((relative, info));
        }

        matched.Sort(static (a, b) => string.CompareOrdinal(a.Relative, b.Relative));

        var afterCursor = cursorPath is null
            ? matched
            : matched.Where(m => string.CompareOrdinal(m.Relative, cursorPath) > 0).ToList();
        var page = afterCursor.Take(pageSize).ToList();
        var truncated = afterCursor.Count > page.Count;

        var items = page.Select(m =>
        {
            var isDirectory = m.Info is DirectoryInfo;
            return new VaultFileEntry(
                m.Relative,
                isDirectory,
                isDirectory ? null : ((FileInfo)m.Info).Length,
                new DateTimeOffset(m.Info.LastWriteTimeUtc, TimeSpan.Zero),
                query.IncludeSha && !isDirectory ? Sha256Of(m.Info.FullName) : null);
        }).ToList();

        var generationEnd = generation.Current;
        return new QueryEnvelope<VaultFileEntry>(
            items,
            truncated,
            truncated ? QueryCursor.Encode(fingerprint, page[^1].Relative) : null,
            scannedFiles,
            items.Count,
            matched.Count, // the full walk completed, so the total is known even mid-pagination
            generationStart,
            generationEnd,
            generationEnd != generationStart);
    }

    /// <summary>Sorted full walk for other services (frontmatter search) needing a deterministic candidate list.</summary>
    internal List<(string Relative, string Absolute)> CollectFilesSorted(
        string? pathPrefix, Func<string, bool> filter, CancellationToken ct)
    {
        var (rootAbsolute, prefixRelative) = ResolvePrefix(pathPrefix);
        var deadline = Environment.TickCount64 + options.QueryTimeoutMs;
        var files = Walk(new DirectoryInfo(rootAbsolute), prefixRelative, ct, deadline)
            .Where(e => e.Info is FileInfo && filter(e.Relative))
            .Select(e => (e.Relative, e.Info.FullName))
            .ToList();
        files.Sort(static (a, b) => string.CompareOrdinal(a.Relative, b.Relative));
        return files;
    }

    private (string Absolute, string Relative) ResolvePrefix(string? pathPrefix)
    {
        if (string.IsNullOrEmpty(pathPrefix))
            return (resolver.Root, "");
        var vp = resolver.Resolve(pathPrefix);
        if (!Directory.Exists(vp.Absolute))
        {
            throw new KnapperException(VaultErrorCode.NotFound,
                $"path prefix does not exist or is not a directory: {vp.Relative}");
        }
        return (vp.Absolute, vp.Relative);
    }

    private IEnumerable<(string Relative, FileSystemInfo Info)> Walk(
        DirectoryInfo directory, string relativeBase, CancellationToken ct, long deadline)
    {
        ct.ThrowIfCancellationRequested();
        if (Environment.TickCount64 >= deadline)
        {
            throw new KnapperException(VaultErrorCode.QueryTimeout,
                $"file listing exceeded {options.QueryTimeoutMs} ms — narrow the scope with a path prefix");
        }

        var children = directory.EnumerateFileSystemInfos()
            .Where(e => !e.Name.StartsWith('.')) // hidden = invisible, at every depth
            .Where(e => (e.Attributes & FileAttributes.ReparsePoint) == 0) // symlinks: never followed, never listed
            .OrderBy(e => e.Name, StringComparer.Ordinal);

        foreach (var child in children)
        {
            var relative = relativeBase.Length == 0 ? child.Name : relativeBase + '/' + child.Name;
            yield return (relative, child);
            if (child is DirectoryInfo subdir)
            {
                foreach (var nested in Walk(subdir, relative, ct, deadline))
                    yield return nested;
            }
        }
    }

    private static string ExtensionOf(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot < 0 ? "" : name[(dot + 1)..].ToLowerInvariant();
    }

    private static string Sha256Of(string absolutePath)
    {
        using var stream = File.OpenRead(absolutePath);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private static IReadOnlyList<string> NormalizeExtensions(IReadOnlyList<string>? extensions) =>
        extensions is null or []
            ? []
            : extensions.Select(e => e.TrimStart('.').ToLowerInvariant()).ToList();
}
