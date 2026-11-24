using Knapper.Core.Mutation;
using Knapper.Core.Query;

namespace Knapper.Core.Tests.Mutation;

/// <summary>
/// A missing array or a null element is a malformed REQUEST, and Core owes it
/// the same typed <c>[InvalidArgument]</c> as every other one. A null that
/// slips through dereferences as a NullReferenceException, which the tool
/// layer can only report as <c>[Internal]</c> — a server bug to the agent
/// reading it, when the fix is in its own arguments (security review
/// 2026-11-24, F10).
/// </summary>
public sealed class NullArgumentTests : IDisposable
{
    private readonly MutationVault _v = new();
    public void Dispose() => _v.Dispose();

    private static void Refused(Action call) =>
        Should.Throw<KnapperException>(call).Code.ShouldBe(VaultErrorCode.InvalidArgument);

    [Fact]
    public void Edit_refuses_a_missing_edits_array_and_a_null_edit()
    {
        var sha = _v.Write("Notes/a.md", "base\n");

        Refused(() => _v.Service.Edit("Notes/a.md", sha, null!));
        Refused(() => _v.Service.Edit("Notes/a.md", sha, [null!]));
        Refused(() => _v.Service.Edit("Notes/a.md", sha, [new EditSpec("base", "x", 1)], [null!]));
        _v.ReadText("Notes/a.md").ShouldBe("base\n");
    }

    [Fact]
    public void Batch_refuses_a_missing_items_array_a_null_item_and_a_null_edit_inside_one()
    {
        var sha = _v.Write("Notes/a.md", "base\n");

        Refused(() => _v.Service.Batch(null!));
        Refused(() => _v.Service.Batch([null!]));
        Refused(() => _v.Service.Batch(
            [new BatchItem(BatchItemKind.Create, "Notes/new.md", null, null, null, "text\n"), null!]));
        File.Exists(_v.Absolute("Notes/new.md")).ShouldBeFalse("a batch with a null item must fail untouched");

        Refused(() => _v.Service.Batch(
            [new BatchItem(BatchItemKind.Edit, "Notes/a.md", sha, [null!], null, null)]));
        _v.ReadText("Notes/a.md").ShouldBe("base\n");
    }

    [Fact]
    public void Batch_read_refuses_a_missing_items_array_and_a_null_item()
    {
        _v.Write("Notes/a.md", "base\n");
        var reader = new VaultReadService(_v.Resolver, _v.Options, _v.Generation);

        Refused(() => reader.BatchRead(null!));
        Refused(() => reader.BatchRead([new VaultReadRequest("Notes/a.md", null, null), null!]));
    }
}
