using Knapper.Core.Generation;
using Knapper.Core.Options;
using Knapper.Core.Query;
using Knapper.Core.Vault;

namespace Knapper.Core.Tests.Query;

/// <summary>
/// A fixture built from the findings the 2026-08-30 pass over Helios actually
/// produced, not from invented ones: a heading that gained a parenthetical
/// and broke five inbound links, a link to '#Backups' where the heading is
/// 'Step 8 — Backups (VM)', an incomplete path under the wrong root, two
/// notes sharing the basename 'Cabinets', a script attachment used as a link
/// target, and plain-text names accidentally bracketed.
/// </summary>
public sealed class LintFixtureVault : IDisposable
{
    public TempDir Dir { get; } = new();
    public VaultLintService Lint { get; }
    public VaultGenerationCounter Generation { get; } = new();

    public LintFixtureVault()
    {
        Dir.File("Notes/Hub.md", string.Join('\n',
        [
            "# Hub",
            "Fine: [[Mailvec Stack]] and [[Mailvec Stack#Step 8 — Backups (VM)]].",
            "Stale anchor: [[Mailvec Stack#Backups]].",
            "Renamed heading: [[Windows Utility VM#Measured throughput — 2026-08-11]].",
            "Incomplete path: [[Home Assistant/InfluxDB Migration Plan]].",
            "Ambiguous: [[Cabinets]].",
            "Bracketed plain text: [[La-Z-Boy]].",
            "Attachment: [[pg-dump-backup.sh]].",
            "Alias: [[Tempest]].",
            "Embed of something absent: ![[missing-diagram.png]].",
            "Into an unreadable note: [[legacy#Some Heading]].",
            "Same file: [[#Hub]] and [[#Nope]].",
            "",
            "| note | why |",
            "|---|---|",
            "| [[Mailvec Stack|MS]] | unescaped pipe |",
            "| [[Mailvec Stack\\|MS]] | escaped, fine |",
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
        Dir.File("Tech/Homelab/Homelab.md", string.Join('\n',
        [
            "# Homelab",
            "## Remote access — [[Tailscale Remote Access]]",
            "### Pipe | heading",
            "",
        ]));
        Dir.File("Tech/Homelab/Tailscale Remote Access.md", "# Tailscale\n");
        Dir.File("Tech/Homelab/Proxmox/Monthly Maintenance.md", "# Monthly\n");
        Dir.File("Cases/Heading Links.md", string.Join('\n',
        [
            "Display text: [[Homelab#Remote access — Tailscale Remote Access]].",
            // '\|' separates the alias here too, so the anchor is checked
            // against the heading and the display text is dropped.
            "Aliased: [[Homelab#Remote access — Tailscale Remote Access\\|the roadmap]].",
            // The OPEN case, pinned: the heading genuinely contains a '|' and
            // the link escapes it. Under §8's rule the escape separates an
            // alias, so the fragment ends early and this IS reported.
            "Literal pipe: [[Homelab#Pipe \\| heading]].",
            "",
        ]));
        // [[CLAUDE]] matches two files only because lookup is case-insensitive.
        Dir.File("CLAUDE.md", "# Instructions\n");
        Dir.File("Tech/Claude.md", "# Claude notes\n");
        Dir.File("Cases/Exact Case.md", "Settled by exact case: [[CLAUDE]].\n");
        // A path relative to the LINKING note's folder, not the vault root.
        Dir.File("Tech/Homelab/Relative Path.md", "Relative: [[Proxmox/Monthly Maintenance]].\n");
        // Two notes share this basename, but the linking note sits in one of
        // their folders — Obsidian resolves to the nearest.
        Dir.File("Kitchen/Nearest.md", "Nearest wins: [[Cabinets]].\n");

        Dir.File("Tech/Homelab/Mailvec Stack.md",
            "# Mailvec Stack\n## Step 8 — Backups (VM)\nbody\n");
        Dir.File("Tech/Homelab/Windows Utility VM.md",
            "# Windows Utility VM\n## Measured throughput — 2026-08-11 (historical — single Crucial P3 Plus)\nbody\n");
        Dir.File("Tech/Home Assistant/InfluxDB Migration Plan.md", "# Plan\nbody\n");
        Dir.File("Kitchen/Cabinets.md", "# Kitchen cabinets\n");
        Dir.File("Laundry/Cabinets.md", "# Laundry cabinets\n");
        Dir.File("scripts/pg-dump-backup.sh", "#!/bin/sh\necho backup\n");
        Dir.File("Aliased/Weather Station.md", "---\naliases: [Tempest, Sky]\n---\n# Weather Station\n");
        // Out of scope for a "Notes" run, and carrying its own broken link.
        Dir.File("Other/Elsewhere.md", "[[No Such Note]] and [[Mailvec Stack]]\n");
        // A .md that is a valid link TARGET but whose text cannot be read:
        // its headings are unknown, so anchors into it must not be judged.
        File.WriteAllBytes(Dir.File("legacy.md"), [.. "caf"u8, 0xE9, .. " legacy\n"u8]);

        // The table-blank-line corpus, from the 2026-09-01 sweep of Helios:
        // one real finding (a paragraph directly above the header row, as in
        // 'Screened Porch Project.md'), and beside it every neighbouring
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
            "| [[Mailvec Stack|MS]] | unescaped, and harmless while this is prose |",
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
        var lister = new VaultFileLister(resolver, Generation, options);
        var reader = new VaultReadService(resolver, options, Generation);
        Lint = new VaultLintService(resolver, lister, reader, Generation, options);
    }

    public void Dispose()
    {
        Generation.Dispose();
        Dir.Dispose();
    }
}
