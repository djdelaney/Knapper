using Knapper.Core.Vault;

namespace Knapper.Core.Tests.Vault;

/// <summary>
/// The predicate behind <c>Vault:ArchivedPrefixes</c>. Every case here is one
/// where a bare <c>StartsWith</c> or a case-folding compare gets it wrong, and
/// gets it wrong SILENTLY — the wrong answer is a folder that quietly stops
/// being searched, or an archive that quietly stops being protected.
/// </summary>
public class ArchivedPrefixesTests
{
    [Fact]
    public void A_prefix_claims_its_own_subtree_and_nothing_that_merely_starts_with_it()
    {
        var archived = new ArchivedPrefixes(["Archive"]);

        archived.Covers("Archive").ShouldBeTrue();               // the directory itself
        archived.Covers("Archive/note.md").ShouldBeTrue();
        archived.Covers("Archive/2024/deep/note.md").ShouldBeTrue();

        // The separator is what stops the prefix spreading to its siblings.
        // "Archived Recipes" is an ordinary folder an ordinary vault has, and
        // hiding it would be undetectable from any response.
        archived.Covers("Archived Recipes/pie.md").ShouldBeFalse();
        archived.Covers("Archive-old/note.md").ShouldBeFalse();
        archived.Covers("Archives/note.md").ShouldBeFalse();
        archived.Covers("Notes/Archive/note.md").ShouldBeFalse(); // prefixes are rooted
    }

    [Fact]
    public void Matching_is_case_sensitive_because_the_vault_filesystem_is()
    {
        // A case-insensitive compare here would hide a directory nobody
        // named, and would put this surface at odds with vault_files, which
        // compares ordinally. ext4 legitimately hosts both spellings; the
        // deployment REQUIRES a case-sensitive filesystem for that reason.
        var archived = new ArchivedPrefixes(["Archive"]);

        archived.Covers("archive/note.md").ShouldBeFalse();
        archived.Covers("ARCHIVE/note.md").ShouldBeFalse();
    }

    [Fact]
    public void Naming_an_archived_prefix_as_the_query_scope_excludes_nothing()
    {
        var archived = new ArchivedPrefixes(["Archive", "Old"]);

        // Reaching archived content is exactly this: name it. Reporting a
        // skip that did not happen misleads as badly as hiding one that did.
        archived.ExcludedFor("Archive").Prefixes.ShouldBe(["Old"]);
        archived.ExcludedFor("Archive/2024").Prefixes.ShouldBe(["Old"]);
        archived.ExcludedFor((string?)null).Prefixes.ShouldBe(["Archive", "Old"]);
        archived.ExcludedFor("Notes").Prefixes.ShouldBe(["Archive", "Old"]);

        // Several scopes: a prefix is skipped only if NO scope opts into it.
        archived.ExcludedFor(["Notes", "Old/2023"]).Prefixes.ShouldBe(["Archive"]);
    }

    [Fact]
    public void A_nested_prefix_is_folded_away_rather_than_reported_twice()
    {
        // Both exclude the same files, so keeping both would report the same
        // subtree twice in excludedPrefixes.
        new ArchivedPrefixes(["Archive", "Archive/2024"]).Prefixes.ShouldBe(["Archive"]);
    }

    [Fact]
    public void Configuration_is_normalized_and_malformed_entries_are_refused()
    {
        new ArchivedPrefixes(["  /Archive/  "]).Prefixes.ShouldBe(["Archive"]);
        new ArchivedPrefixes([""]).Prefixes.ShouldBeEmpty();   // an empty element means "none"
        new ArchivedPrefixes(null).Any.ShouldBeFalse();

        // Loud at boot, because the quiet failure is an archive the operator
        // believes is protected and is not.
        foreach (var bad in new[] { "/", "..", "Notes/../x", ".hidden", "Notes/.git" })
        {
            var ex = Should.Throw<KnapperException>(() => new ArchivedPrefixes([bad]));
            ex.Code.ShouldBe(VaultErrorCode.InvalidPath);
            ex.Message.ShouldContain("Vault:ArchivedPrefixes");
        }
    }
}
