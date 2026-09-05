using Knapper.Core.Mutation;
using Knapper.Core.Vault;

namespace Knapper.Core.Tests.Mutation;

/// <summary>
/// The write half of <c>Vault:ArchivedPrefixes</c>: what is already in an
/// archived subtree is immutable, and putting something INTO one is not.
///
/// <para>That asymmetry is the design, not an oversight. An archive is filled
/// by writing to it — a note is trimmed down and its superseded version filed
/// — so a blanket ban under the prefix would have banned the workflow the
/// setting exists to serve, and the protection would have been abandoned the
/// first time it got in the way.</para>
/// </summary>
public class ArchivedMutationTests : IClassFixture<MutationVault>
{
    private readonly MutationVault _v;
    private readonly VaultMutationService _service;

    public ArchivedMutationTests(MutationVault v)
    {
        _v = v;
        _service = v.ServiceWithArchived("Archive");
    }

    [Fact]
    public void Changing_what_is_already_archived_is_refused_and_audited()
    {
        var sha = _v.Write("Archive/Old.md", "superseded content\n");

        foreach (var attempt in new (string What, Func<object> Act)[]
        {
            ("edit", () => _service.Edit("Archive/Old.md", sha, [new EditSpec("superseded", "current")])),
            ("append", () => _service.Append("Archive/Old.md", sha, "more\n")),
            ("delete", () => _service.Delete("Archive/Old.md", sha)),
            ("move out", () => _service.Move("Archive/Old.md", "Notes/Back.md", sha)),
        })
        {
            var ex = Should.Throw<KnapperException>(attempt.Act);
            ex.Code.ShouldBe(VaultErrorCode.PathArchived, attempt.What);
            // The message has to say what IS allowed; a refusal an agent
            // cannot act on turns into a retry loop or a workaround.
            ex.Message.ShouldContain("Creating and moving files INTO");
        }

        // Untouched on disk, and the refusals are in the trail: a rejection is
        // signal, exactly like a stale-write rejection.
        File.ReadAllText(Path.Combine(_v.Resolver.Root, "Archive/Old.md")).ShouldBe("superseded content\n");
        _v.AuditLines().ShouldContain(l => l.Contains("Archive/Old.md") && l.Contains("PathArchived"));
    }

    [Fact]
    public void Filing_a_superseded_copy_into_the_archive_still_works()
    {
        // A prefix of its own, built from nothing, so this covers the case
        // that matters most: an archive root that does not exist yet must be
        // creatable THROUGH the protection, or the setting can never be
        // turned on for a folder an agent is expected to maintain.
        var service = _v.ServiceWithArchived("Filed");

        service.CreateDirectory("Filed");
        service.CreateDirectory("Filed/2025");
        service.Create("Filed/2025/Snapshot.md", "the old version\n");

        var sha = _v.Write("Notes/Retire.md", "to be archived\n");
        var moved = service.Move("Notes/Retire.md", "Filed/2025/Retired.md", sha);

        moved.Verified.ShouldBeTrue();
        File.Exists(Path.Combine(_v.Resolver.Root, "Filed/2025/Retired.md")).ShouldBeTrue();
        File.Exists(Path.Combine(_v.Resolver.Root, "Notes/Retire.md")).ShouldBeFalse();
    }

    [Fact]
    public void A_batch_carrying_an_archived_edit_fails_whole_and_untouched()
    {
        var live = _v.Write("Notes/Batch.md", "live alpha\n");
        var archivedSha = _v.Write("Archive/Batch.md", "archived alpha\n");

        var ex = Should.Throw<KnapperException>(() => _service.Batch(
        [
            new BatchItem(BatchItemKind.Edit, "Notes/Batch.md", live, [new EditSpec("alpha", "beta")]),
            new BatchItem(BatchItemKind.Edit, "Archive/Batch.md", archivedSha, [new EditSpec("alpha", "beta")]),
        ]));

        ex.Code.ShouldBe(VaultErrorCode.PathArchived);
        // Refused in VALIDATE, so the legal item ahead of it never ran.
        ex.Message.ShouldContain("nothing was mutated");
        File.ReadAllText(Path.Combine(_v.Resolver.Root, "Notes/Batch.md")).ShouldBe("live alpha\n");
        File.ReadAllText(Path.Combine(_v.Resolver.Root, "Archive/Batch.md")).ShouldBe("archived alpha\n");
    }

    [Fact]
    public void A_batch_may_still_create_into_the_archive()
    {
        var live = _v.Write("Notes/Trim.md", "long history alpha\n");

        var result = _service.Batch(
        [
            new BatchItem(BatchItemKind.Edit, "Notes/Trim.md", live, [new EditSpec("long history ", "")]),
            new BatchItem(BatchItemKind.Create, "Archive/Trimmed.md", Text: "long history alpha\n"),
        ]);

        // One round trip for the whole trim-and-archive, which is why create
        // is exempt on this surface as well as the single-item one.
        result.Items.ShouldAllBe(i => i.Status == BatchItemStatus.Applied);
    }

    [Fact]
    public void A_folder_that_merely_starts_with_the_prefix_is_untouched_by_any_of_it()
    {
        var sha = _v.Write("Archived Recipes/Pie.md", "pastry\n");

        var result = _service.Edit("Archived Recipes/Pie.md", sha, [new EditSpec("pastry", "shortcrust")]);

        result.Verified.ShouldBeTrue();
    }
}
