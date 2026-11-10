using Knapper.Core.Generation;
using Knapper.Core.Options;
using Knapper.Core.Query;
using Knapper.Core.Vault;

namespace Knapper.Core.Tests.Query;

/// <summary>
/// A fixture whose SHAPES are the findings a real lint pass produced
/// (2026-08-30), not ones imagined in advance. Every name, path and heading
/// in it is INVENTED — this repository is public, and a fixture is never
/// built from a real vault's content. The shapes: a heading that gained a parenthetical
/// and broke five inbound links, a link to '#Backups' where the heading is
/// 'Step 8 — Backups (VM)', a partial path that pass called broken and that
/// Obsidian in fact follows by its trailing segments (beside wrong-parent and
/// mid-name spellings that stay broken), notes sharing the basename
/// 'Shelving', a script attachment used as a link target, and plain-text names
/// accidentally bracketed.
/// </summary>
public sealed class LintFixtureVault : IDisposable
{
    public TempDir Dir { get; } = new();
    public VaultLintService Lint { get; }
    public VaultGenerationCounter Generation { get; } = new();

    public LintFixtureVault() : this(ArchivedPrefixes.None) { }

    /// <summary>See FixtureVault: a class fixture must construct with no arguments.</summary>
    public static LintFixtureVault WithArchived(params string[] prefixes) =>
        new(new ArchivedPrefixes(prefixes));

    private LintFixtureVault(ArchivedPrefixes archived)
    {
        Dir.File("Notes/Hub.md", string.Join('\n',
        [
            "# Hub",
            "Fine: [[Mail Stack]] and [[Mail Stack#Step 8 — Backups (VM)]].",
            "Stale anchor: [[Mail Stack#Backups]].",
            "Renamed heading: [[Bench Grinder#Measured throughput — 2026-08-11]].",
            "Path suffix: [[Field Station/Sensor Migration Plan]]; wrong parent: " +
                "[[Wrong Parent/Sensor Migration Plan]]; mid-name: [[Station/Sensor Migration Plan]].",
            "Ambiguous: [[Shelving]].",
            "Bracketed plain text: [[Wing-Back]].",
            "Attachment: [[nightly-backup.sh]].",
            "Alias: [[Skywatch]].",
            "Embed of something absent: ![[missing-diagram.png]].",
            "Into an unreadable note: [[legacy#Some Heading]].",
            "Same file: [[#Hub]] and [[#Nope]].",
            "",
            "| note | why |",
            "|---|---|",
            "| [[Mail Stack|MS]] | unescaped pipe |",
            "| [[Mail Stack\\|MS]] | escaped, fine |",
            "",
            "```sh",
            "if [[ -t 1 ]]; then echo tty; fi   # not a link",
            "# not a heading either",
            "```",
            "",
        ]));

        // Classes discovered by running against the real vault, 2026-08-30.
        // A heading whose text CONTAINS a wikilink: Obsidian anchors it by the
        // display text, and five Helios links spell it that way.
        Dir.File("Projects/Workshop/Workshop.md", string.Join('\n',
        [
            "# Workshop",
            "## Remote access — [[VPN Remote Access]]",
            "### Pipe | heading",
            "",
        ]));
        Dir.File("Projects/Workshop/VPN Remote Access.md", "# VPN\n");
        Dir.File("Projects/Workshop/Tools/Monthly Maintenance.md", "# Monthly\n");
        Dir.File("Cases/Heading Links.md", string.Join('\n',
        [
            "Display text: [[Workshop#Remote access — VPN Remote Access]].",
            // '\|' separates the alias here too, so the anchor is checked
            // against the heading and the display text is dropped.
            "Aliased: [[Workshop#Remote access — VPN Remote Access\\|the roadmap]].",
            // The OPEN case, pinned: the heading genuinely contains a '|' and
            // the link escapes it. Under §8's rule the escape separates an
            // alias, so the fragment ends early and this IS reported.
            "Literal pipe: [[Workshop#Pipe \\| heading]].",
            "",
        ]));
        // [[CLAUDE]] matches two files only because lookup is case-insensitive.
        Dir.File("CLAUDE.md", "# Instructions\n");
        Dir.File("Tech/Claude.md", "# Claude notes\n");
        Dir.File("Cases/Exact Case.md", "Settled by exact case: [[CLAUDE]].\n");
        // A path relative to the LINKING note's folder, not the vault root.
        Dir.File("Projects/Workshop/Relative Path.md", "Relative: [[Tools/Monthly Maintenance]].\n");
        // Two notes share this basename, but the linking note sits in one of
        // their folders — Obsidian resolves to the nearest.
        Dir.File("Garage/Nearest.md", "Nearest wins: [[Shelving]].\n");

        Dir.File("Projects/Workshop/Mail Stack.md",
            "# Mail Stack\n## Step 8 — Backups (VM)\nbody\n");
        Dir.File("Projects/Workshop/Bench Grinder.md",
            "# Bench Grinder\n## Measured throughput — 2026-08-11 (historical — single old motor)\nbody\n");
        Dir.File("Tech/Field Station/Sensor Migration Plan.md", "# Plan\nbody\n");
        Dir.File("Garage/Shelving.md", "# Garage shelving\n");
        Dir.File("Attic/Shelving.md", "# Attic shelving\n");
        // A THIRD Shelving, nested, linked by naming its own folder from
        // inside it — the Helios spelling Obsidian follows.
        Dir.File("Home/Shed and Greenhouse/Shelving.md", "# Greenhouse shelving\n");
        Dir.File("Home/Shed and Greenhouse/Floor Mat.md", "For the [[Shed and Greenhouse/Shelving]] install.\n");
        Dir.File("scripts/nightly-backup.sh", "#!/bin/sh\necho backup\n");
        Dir.File("Aliased/Weather Station.md", "---\naliases: [Skywatch, Sky]\n---\n# Weather Station\n");
        // Out of scope for a "Notes" run, and carrying its own broken link.
        Dir.File("Other/Elsewhere.md", "[[No Such Note]] and [[Mail Stack]]\n");
        // A .md that is a valid link TARGET but whose text cannot be read:
        // its headings are unknown, so anchors into it must not be judged.
        File.WriteAllBytes(Dir.File("legacy.md"), [.. "caf"u8, 0xE9, .. " legacy\n"u8]);

        // The table-blank-line corpus, from the 2026-09-01 sweep of Helios:
        // one real finding (a paragraph directly above the header row, as in
        // 'Shed Plan.md'), and beside it every neighbouring
        // shape that must stay silent.
        Dir.File("Tables/Absorbed.md", string.Join('\n',
        [
            "# Tables",                                     // 1
            "Intro paragraph with nothing between it and the table.",
            "| Yard | Minimum |",                           // 3 — a paragraph absorbs it
            "|---|---|",
            "| Rear | 50 ft |",
            "",
            "## Under a heading",                           // 7 — a heading closes the block
            "| Item | Value |",
            "|---|---|",
            "",
            "A paragraph, properly separated.",             // 11
            "",
            "| Spaced | Header |",                          // 13 — the blank line is there
            "|---|---|",
            "",
            "- A list item, with a space-indented table under it",
            "    | Tag | Nodes |",                          // 17 — a bullet absorbs it too
            "    |---|---|",
            "",
            "- A list item, with a tab-indented table under it",
            "\t| Tag | Nodes |",                            // 21 — Obsidian's own indent
            "\t|---|---|",
            "",
            "An indented code block, which is code rather than a table:",
            "",
            "    sqlite3 db 'select 1'",
            "    | a | b |",                                // 27 — code: the pipes are literal
            "    |---|---|",
            "",
            "```md",
            "An example in a fence:",
            "| a | b |",                                    // 32 — an example, not a table
            "|---|---|",
            "```",
            "",
            "- A bullet whose example is in an INDENTED fence",
            "    ```md",
            "    | a | b |",                                // 38 — still an example
            "    |---|---|",
            "    ```",
            "",
        ]));
        // An absorbed block is not a table, so the pipe inside this wikilink
        // opens no column and table_pipe must not report it.
        Dir.File("Tables/Pipe.md", string.Join('\n',
        [
            "Paragraph with nothing between it and the table.",
            "| note | why |",                               // 2 — the finding
            "|---|---|",
            "| [[Mail Stack|MS]] | unescaped, and harmless while this is prose |",
            "",
        ]));

        // One wikilink, two findings, ONE position: the pipe opens a phantom
        // column and the target resolves to nothing. The header row starts
        // the file, so the block really is a table.
        Dir.File("Collision/Both.md", string.Join('\n',
        [
            "| note | why |",
            "|---|---|",
            "| [[No Such Table Note|Alias]] | table_pipe and unresolved_link, same line and column |",
            "",
        ]));

        var resolver = new VaultPathResolver(Dir.Path);
        var options = new VaultOptions { RootPath = resolver.Root };
        var lister = new VaultFileLister(resolver, Generation, options, archived);
        var reader = new VaultReadService(resolver, options, Generation);
        Lint = new VaultLintService(resolver, lister, reader, Generation, options, archived);
    }

    public void Dispose()
    {
        Generation.Dispose();
        Dir.Dispose();
    }
}
