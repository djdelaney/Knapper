using System.Text;
using Knapper.Core.Generation;
using Knapper.Core.Options;
using Knapper.Core.Query;
using Knapper.Core.Vault;

namespace Knapper.Core.Tests.Query;

/// <summary>
/// A generated fixture vault exercising the §13 equivalence axes: unicode
/// names/content, spaces in paths, nesting, binary and non-UTF-8 files,
/// hidden/control-dir decoys that must stay invisible, frontmatter variants
/// (incl. broken YAML), and enough matches to force multi-page results.
/// </summary>
public sealed class FixtureVault : IDisposable
{
    public TempDir Dir { get; } = new();
    public VaultPathResolver Resolver { get; }
    public VaultOptions Options { get; }
    public VaultGenerationCounter Generation { get; } = new();
    public VaultSearchService Search { get; }
    public VaultFileLister Lister { get; }
    public VaultReadService Reader { get; }
    public FrontmatterSearchService Frontmatter { get; }

    /// <summary>All non-hidden FILE paths in the fixture, ordinal-sorted.</summary>
    public static readonly string[] VisibleFiles =
    [
        "Notes/Daily.md",
        "Notes/Sub/Deep.md",
        "Projects/pröject.md",
        "empty.md",
        "fm/a.md",
        "fm/b.md",
        "fm/broken.md",
        "fm/none.md",
        "fm/unterminated.md",
        "latin1/legacy.md",
        "many/needles-0.md",
        "many/needles-1.md",
        "many/needles-2.md",
        "many/needles-3.md",
        "raw/blob.bin",
        "scripts/backup.sh",
        "with spaces/nöte – ünïcode.md",
    ];

    public FixtureVault()
    {
        Dir.File("Notes/Daily.md", "# Daily\nTODO alpha task\ntodo beta task\nDone gamma\nwrap TODO up\n");
        Dir.File("Notes/Sub/Deep.md", "deep content\nneedle here\n");
        Dir.File("Projects/pröject.md", "Ünïcode käse content\nneedle in pröject\n");
        Dir.File("empty.md", "");
        Dir.File("fm/a.md", "---\ntags: [alpha, beta]\nstatus: active\n---\nbody a\n");
        Dir.File("fm/b.md", "---\nstatus: Archived\ntitle: \"B note\"\n---\nbody b\n");
        Dir.File("fm/broken.md", "---\nstatus: [unclosed\n---\nbody broken\n");
        Dir.File("fm/none.md", "no frontmatter here\n");
        // Opening fence, no closing fence: malformed, must reach UnparseableFiles.
        Dir.File("fm/unterminated.md", "---\nstatus: hidden\nbody without a closing fence\n");
        Dir.File("scripts/backup.sh", "#!/bin/sh\necho needle backup\n");
        Dir.File("with spaces/nöte – ünïcode.md", "Ünïcode käse\nneedle ünïcode\n");
        // 4 files x 15 matching lines = 60 'needle' matches → multi-page.
        for (var f = 0; f < 4; f++)
        {
            var sb = new StringBuilder();
            for (var l = 0; l < 15; l++)
                sb.Append($"line {l} needle {f}\n");
            Dir.File($"many/needles-{f}.md", sb.ToString());
        }
        // Binary: NUL bytes around a searchable word — rg must exclude it.
        File.WriteAllBytes(Dir.File("raw/blob.bin"),
            [0x00, 0x01, .. "needle"u8, 0x00, 0xFF]);
        // Non-UTF-8 text (latin1 é) — searchable by rg (lossily), unreadable as strict text.
        File.WriteAllBytes(Dir.File("latin1/legacy.md"),
            [.. "caf"u8, 0xE9, .. " needle legacy\n"u8]);
        // Invisible decoys: control dirs + hidden entries. All contain 'needle'
        // and must appear in NO listing and NO search result.
        Dir.File(".git/config", "needle in git\n");
        Dir.File(".obsidian/app.json", "{\"needle\": true}\n");
        Dir.File(".trash/old.md", "needle in trash\n");
        Dir.File(".hidden.md", "needle hidden\n");
        Dir.File("Notes/.hiddendir/x.md", "needle hidden dir\n");

        Resolver = new VaultPathResolver(Dir.Path);
        Options = new VaultOptions { RootPath = Resolver.Root };
        Search = new VaultSearchService(Resolver, Generation, Options);
        Lister = new VaultFileLister(Resolver, Generation, Options);
        Reader = new VaultReadService(Resolver, Options, Generation);
        Frontmatter = new FrontmatterSearchService(Resolver, Lister, Reader, Generation, Options);
    }

    public void Dispose()
    {
        Generation.Dispose();
        Dir.Dispose();
    }
}
