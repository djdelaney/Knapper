using System.Text.Json;
using Knapper.Core.Mutation;

namespace Knapper.Core.Tests.Mutation;

public sealed class VaultMutationServiceTests : IDisposable
{
    private readonly MutationVault _v = new();

    public void Dispose() => _v.Dispose();

    // ---- edit ----------------------------------------------------------

    [Fact]
    public void Edit_applies_anchored_change_and_reports_verified_shas()
    {
        var sha = _v.Write("Notes/Daily.md", "# Daily\nold line\nrest\n");
        var genBefore = _v.Generation.Current;

        var result = _v.Service.Edit("Notes/Daily.md", sha,
            [new EditSpec("old line", "new line")], guards: ["# Daily"]);

        _v.ReadText("Notes/Daily.md").ShouldBe("# Daily\nnew line\nrest\n");
        result.OldSha256.ShouldBe(sha);
        result.Verified.ShouldBeTrue();
        result.Generation.ShouldBe(genBefore + 1);
        _v.AuditLines().ShouldContain(l => l.Contains("\"Op\":\"edit\"") && l.Contains("\"Outcome\":\"ok\""));
    }

    [Fact]
    public void Edits_apply_sequentially_and_count_semantics_are_exact()
    {
        var sha = _v.Write("n.md", "aaa bbb aaa\n");

        // count=2 replaces both occurrences
        _v.Service.Edit("n.md", sha, [new EditSpec("aaa", "xxx", Count: 2)]);
        _v.ReadText("n.md").ShouldBe("xxx bbb xxx\n");

        // sequential: second edit anchors on the FIRST edit's output
        var sha2 = _v.Service.Edit("n.md",
            Knapper.Core.Vault.VaultHash.Sha256Hex("xxx bbb xxx\n"u8.ToArray()),
            [new EditSpec("bbb", "yyy"), new EditSpec("xxx yyy xxx", "done")]).NewSha256;
        _v.ReadText("n.md").ShouldBe("done\n");
        sha2.ShouldNotBeNull();
    }

    [Fact]
    public void Anchor_count_mismatch_rejects_with_file_untouched()
    {
        var sha = _v.Write("n.md", "dup dup\n");
        Should.Throw<KnapperException>(() =>
                _v.Service.Edit("n.md", sha, [new EditSpec("dup", "x")])) // present twice, count=1
            .Code.ShouldBe(VaultErrorCode.AnchorMismatch);
        _v.ReadText("n.md").ShouldBe("dup dup\n");
    }

    [Fact]
    public void Stale_sha_rejects_untouched_and_the_rejection_is_audited()
    {
        _v.Write("n.md", "current\n");
        var ex = Should.Throw<KnapperException>(() =>
            _v.Service.Edit("n.md", "0".PadLeft(64, '0'), [new EditSpec("current", "x")]));
        ex.Code.ShouldBe(VaultErrorCode.PreconditionFailed);
        ex.Message.ShouldContain("NEVER retry with the old base");
        _v.ReadText("n.md").ShouldBe("current\n");
        _v.AuditLines().ShouldContain(l => l.Contains("PreconditionFailed"));
    }

    [Fact]
    public void Guards_must_exist_before_and_survive_after()
    {
        var sha = _v.Write("n.md", "keep me\nchange me\n");

        Should.Throw<KnapperException>(() => _v.Service.Edit("n.md", sha,
                [new EditSpec("change me", "changed")], guards: ["not present"]))
            .Code.ShouldBe(VaultErrorCode.GuardViolation);

        Should.Throw<KnapperException>(() => _v.Service.Edit("n.md", sha,
                [new EditSpec("keep me", "gone")], guards: ["keep me"]))
            .Code.ShouldBe(VaultErrorCode.GuardViolation);

        _v.ReadText("n.md").ShouldBe("keep me\nchange me\n"); // both rejections mutated nothing
    }

    [Fact]
    public void No_op_edits_and_bad_specs_are_typed()
    {
        var sha = _v.Write("n.md", "aba\n");
        Should.Throw<KnapperException>(() => _v.Service.Edit("n.md", sha, []))
            .Code.ShouldBe(VaultErrorCode.InvalidArgument);
        Should.Throw<KnapperException>(() => _v.Service.Edit("n.md", sha, [new EditSpec("x", "x")]))
            .Code.ShouldBe(VaultErrorCode.InvalidArgument);
        Should.Throw<KnapperException>(() => _v.Service.Edit("n.md", sha, [new EditSpec("a", "b", Count: 0)]))
            .Code.ShouldBe(VaultErrorCode.InvalidArgument);
    }

    [Fact]
    public void Non_utf8_files_refuse_text_mutations()
    {
        File.WriteAllBytes(_v.VaultDir.File("bin.md"), [0xFF, 0xFE, 0x00]);
        var sha = Knapper.Core.Vault.VaultHash.Sha256Hex(new byte[] { 0xFF, 0xFE, 0x00 });
        Should.Throw<KnapperException>(() => _v.Service.Edit("bin.md", sha, [new EditSpec("a", "b")]))
            .Code.ShouldBe(VaultErrorCode.NotUtf8);
    }

    // ---- append / create / mkdir --------------------------------------

    [Fact]
    public void Append_appends_under_the_same_discipline()
    {
        var sha = _v.Write("log.md", "line1\n");
        var result = _v.Service.Append("log.md", sha, "line2\n");
        _v.ReadText("log.md").ShouldBe("line1\nline2\n");
        result.BytesAfter.ShouldBe(12);

        Should.Throw<KnapperException>(() => _v.Service.Append("log.md", sha, "stale\n"))
            .Code.ShouldBe(VaultErrorCode.PreconditionFailed);
        Should.Throw<KnapperException>(() => _v.Service.Append("log.md", result.NewSha256, ""))
            .Code.ShouldBe(VaultErrorCode.InvalidArgument);
    }

    [Fact]
    public void Create_is_no_clobber_and_requires_the_parent()
    {
        _v.Write("dir/.keep", "");
        var result = _v.Service.Create("dir/new.md", "hello\n");
        _v.ReadText("dir/new.md").ShouldBe("hello\n");
        result.OldSha256.ShouldBeNull();

        Should.Throw<KnapperException>(() => _v.Service.Create("dir/new.md", "clobber"))
            .Code.ShouldBe(VaultErrorCode.AlreadyExists);
        _v.ReadText("dir/new.md").ShouldBe("hello\n");

        Should.Throw<KnapperException>(() => _v.Service.Create("no-dir/x.md", "x"))
            .Code.ShouldBe(VaultErrorCode.NotFound);
    }

    [Fact]
    public void CreateDirectory_is_deliberate_and_single_level()
    {
        _v.Service.CreateDirectory("Projects");
        Directory.Exists(Path.Combine(_v.VaultDir.Path, "Projects")).ShouldBeTrue();

        Should.Throw<KnapperException>(() => _v.Service.CreateDirectory("Projects"))
            .Code.ShouldBe(VaultErrorCode.AlreadyExists);
        Should.Throw<KnapperException>(() => _v.Service.CreateDirectory("a/b"))
            .Code.ShouldBe(VaultErrorCode.NotFound);
    }

    // ---- move / delete -------------------------------------------------

    [Fact]
    public void Move_requires_source_hash_and_absent_destination()
    {
        var sha = _v.Write("a.md", "content\n");
        _v.Write("dest/.keep", "");

        var result = _v.Service.Move("a.md", "dest/b.md", sha);
        result.Path.ShouldBe("dest/b.md");
        _v.ReadText("dest/b.md").ShouldBe("content\n");
        File.Exists(Path.Combine(_v.VaultDir.Path, "a.md")).ShouldBeFalse();

        var sha2 = _v.Write("c.md", "other\n");
        Should.Throw<KnapperException>(() => _v.Service.Move("c.md", "dest/b.md", sha2))
            .Code.ShouldBe(VaultErrorCode.AlreadyExists);
        Should.Throw<KnapperException>(() => _v.Service.Move("c.md", "nowhere/d.md", sha2))
            .Code.ShouldBe(VaultErrorCode.NotFound);
        Should.Throw<KnapperException>(() => _v.Service.Move("c.md", "d.md", "0".PadLeft(64, '0')))
            .Code.ShouldBe(VaultErrorCode.PreconditionFailed);
        _v.ReadText("c.md").ShouldBe("other\n"); // every rejection left it in place
    }

    [Fact]
    public void Delete_is_soft_lands_in_trash_and_keeps_structure()
    {
        var sha = _v.Write("Notes/old.md", "goodbye\n");
        var result = _v.Service.Delete("Notes/old.md", sha);

        result.TrashPath.ShouldBe(".trash/Notes/old.md");
        File.Exists(Path.Combine(_v.VaultDir.Path, "Notes/old.md")).ShouldBeFalse();
        _v.ReadText(".trash/Notes/old.md").ShouldBe("goodbye\n");

        // A second delete of a recreated file must not overwrite the first trash copy.
        var sha2 = _v.Write("Notes/old.md", "second life\n");
        var second = _v.Service.Delete("Notes/old.md", sha2);
        second.TrashPath.ShouldNotBe(".trash/Notes/old.md");
        second.TrashPath.ShouldStartWith(".trash/Notes/old-");
        _v.ReadText(".trash/Notes/old.md").ShouldBe("goodbye\n");
        _v.ReadText(second.TrashPath).ShouldBe("second life\n");
    }

    [Fact]
    public void Delete_requires_the_hash()
    {
        _v.Write("n.md", "x\n");
        Should.Throw<KnapperException>(() => _v.Service.Delete("n.md", "0".PadLeft(64, '0')))
            .Code.ShouldBe(VaultErrorCode.PreconditionFailed);
        _v.ReadText("n.md").ShouldBe("x\n");
    }

    // ---- gates ---------------------------------------------------------

    [Fact]
    public void A_conflict_sibling_blocks_original_and_sibling_but_not_neighbors()
    {
        var shaDaily = _v.Write("Notes/Daily.md", "original\n");
        var shaConflict = _v.Write("Notes/Daily (Conflicted copy 2026-08-08 120000).md", "conflicted\n");
        var shaDail = _v.Write("Notes/Dail.md", "similar stem\n");

        Should.Throw<KnapperException>(() =>
                _v.Service.Edit("Notes/Daily.md", shaDaily, [new EditSpec("original", "x")]))
            .Code.ShouldBe(VaultErrorCode.MutationBlocked);
        Should.Throw<KnapperException>(() =>
                _v.Service.Edit("Notes/Daily (Conflicted copy 2026-08-08 120000).md", shaConflict,
                    [new EditSpec("conflicted", "x")]))
            .Code.ShouldBe(VaultErrorCode.MutationBlocked);

        // A different stem that happens to share a prefix is NOT blocked.
        _v.Service.Edit("Notes/Dail.md", shaDail, [new EditSpec("similar stem", "edited")]);
        _v.ReadText("Notes/Dail.md").ShouldBe("edited\n");
    }

    [Fact]
    public void Conflict_scan_finds_conflict_files()
    {
        _v.Write("Notes/Daily.md", "x");
        _v.Write("Notes/Daily (Conflicted copy 2026).md", "y");
        _v.Conflicts.ScanAll().ShouldBe(["Notes/Daily (Conflicted copy 2026).md"]);
    }

    [Fact]
    public void A_closed_sync_gate_fails_every_mutation_closed()
    {
        var sha = _v.Write("n.md", "content\n");
        var blocked = _v.BlockedService;
        Should.Throw<KnapperException>(() => blocked.Edit("n.md", sha, [new EditSpec("content", "x")]))
            .Code.ShouldBe(VaultErrorCode.MutationBlocked);
        Should.Throw<KnapperException>(() => blocked.Create("new.md", "x"))
            .Code.ShouldBe(VaultErrorCode.MutationBlocked);
        Should.Throw<KnapperException>(() => blocked.Delete("n.md", sha))
            .Code.ShouldBe(VaultErrorCode.MutationBlocked);
        _v.ReadText("n.md").ShouldBe("content\n");
    }

    // ---- batch ---------------------------------------------------------

    [Fact]
    public void Batch_applies_mixed_operations()
    {
        var shaA = _v.Write("a.md", "alpha\n");
        var shaB = _v.Write("b.md", "beta\n");

        var result = _v.Service.Batch(
        [
            new BatchItem(BatchItemKind.Edit, "a.md", shaA, [new EditSpec("alpha", "ALPHA")]),
            new BatchItem(BatchItemKind.Append, "b.md", shaB, Text: "more\n"),
            new BatchItem(BatchItemKind.Create, "c.md", Text: "gamma\n"),
        ]);

        result.AllApplied.ShouldBeTrue();
        result.Items.ShouldAllBe(i => i.Status == BatchItemStatus.Applied && i.NewSha256 != null);
        _v.ReadText("a.md").ShouldBe("ALPHA\n");
        _v.ReadText("b.md").ShouldBe("beta\nmore\n");
        _v.ReadText("c.md").ShouldBe("gamma\n");
    }

    [Fact]
    public void One_bad_hash_fails_validation_and_nothing_mutates()
    {
        var shaA = _v.Write("a.md", "alpha\n");
        _v.Write("b.md", "beta\n");

        var ex = Should.Throw<KnapperException>(() => _v.Service.Batch(
        [
            new BatchItem(BatchItemKind.Edit, "a.md", shaA, [new EditSpec("alpha", "ALPHA")]),
            new BatchItem(BatchItemKind.Edit, "b.md", "0".PadLeft(64, '0'), [new EditSpec("beta", "BETA")]),
        ]));
        ex.Code.ShouldBe(VaultErrorCode.PreconditionFailed);
        ex.Message.ShouldContain("nothing was mutated");

        _v.ReadText("a.md").ShouldBe("alpha\n"); // the VALID item was not applied either
        _v.ReadText("b.md").ShouldBe("beta\n");
    }

    [Fact]
    public void Duplicate_paths_in_a_batch_are_rejected()
    {
        var sha = _v.Write("a.md", "alpha\n");
        Should.Throw<KnapperException>(() => _v.Service.Batch(
        [
            new BatchItem(BatchItemKind.Edit, "a.md", sha, [new EditSpec("alpha", "x")]),
            new BatchItem(BatchItemKind.Edit, "a.md", sha, [new EditSpec("alpha", "y")]),
        ])).Code.ShouldBe(VaultErrorCode.InvalidArgument);
        _v.ReadText("a.md").ShouldBe("alpha\n");
    }

    // ---- audit ---------------------------------------------------------

    [Fact]
    public void Audit_lines_are_parseable_jsonl_with_before_and_after_shas()
    {
        var sha = _v.Write("n.md", "one\n");
        var result = _v.Service.Edit("n.md", sha, [new EditSpec("one", "two")],
            ctx: new AuditContext("test-client", "req-1"));

        var entries = _v.AuditLines().Select(l => JsonDocument.Parse(l).RootElement).ToList();
        var ok = entries.Single(e => e.GetProperty("Outcome").GetString() == "ok");
        ok.GetProperty("Op").GetString().ShouldBe("edit");
        ok.GetProperty("Path").GetString().ShouldBe("n.md");
        ok.GetProperty("Client").GetString().ShouldBe("test-client");
        ok.GetProperty("RequestId").GetString().ShouldBe("req-1");
        ok.GetProperty("BeforeSha256").GetString().ShouldBe(sha);
        ok.GetProperty("AfterSha256").GetString().ShouldBe(result.NewSha256);
    }
}
