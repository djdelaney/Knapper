using Knapper.Core.Mutation;

namespace Knapper.Core.Tests.Mutation;

/// <summary>
/// The audit contract under a failing sink (remediation review finding 3):
/// intent BEFORE the first write means audit-down refuses mutations fail
/// closed; a post-write append failure preserves the success/batch receipt
/// (the work already landed) and surfaces through the durable audit-failure
/// metric instead — and a failed sink is never written to again from a
/// catch path.
/// </summary>
public sealed class AuditFailureTests : IDisposable
{
    private readonly MutationVault _v = new();

    public void Dispose() => _v.Dispose();

    private void BreakAuditSink() =>
        File.SetUnixFileMode(_v.AuditPath, UnixFileMode.UserRead);

    [Fact]
    public void Audit_unavailable_refuses_the_mutation_before_any_write()
    {
        var sha = _v.Write("Notes/a.md", "original\n");
        _ = _v.Service.Edit("Notes/a.md", sha, [new EditSpec("original", "warmup")]); // creates the audit file
        sha = _v.Write("Notes/a.md", "original\n");
        BreakAuditSink();

        var ex = Should.Throw<KnapperException>(() =>
            _v.Service.Edit("Notes/a.md", sha, [new EditSpec("original", "changed")]));

        ex.Code.ShouldBe(VaultErrorCode.IoError);
        ex.Message.ShouldContain("fail closed");
        _v.ReadText("Notes/a.md").ShouldBe("original\n"); // refused BEFORE any write
        _v.Metrics.Read().AuditAppendFailures.ShouldBeGreaterThan(0); // durable monitor signal
    }

    [Fact]
    public void Post_write_audit_failure_preserves_the_success_receipt()
    {
        var sha = _v.Write("Notes/a.md", "original\n");
        // Fail ONLY the post-write "ok" record: intent lands, the write
        // lands, then the sink dies.
        _v.Audit.BeforeAppendTestHook = e =>
        {
            if (e.Outcome == "ok")
                throw new IOException("sink died after the write");
        };

        var result = _v.Service.Edit("Notes/a.md", sha, [new EditSpec("original", "changed")]);

        result.Verified.ShouldBeTrue(); // landed work reports success, not a false failure
        _v.ReadText("Notes/a.md").ShouldBe("changed\n");
        _v.AuditLines().ShouldContain(l => l.Contains("\"Outcome\":\"attempt\""),
            "the write-ahead intent record still explains the change");
        _v.Metrics.Read().AuditAppendFailures.ShouldBe(1);
    }

    [Fact]
    public void Post_write_audit_failure_mid_batch_preserves_the_receipt()
    {
        var shaA = _v.Write("a.md", "alpha\n");
        var shaB = _v.Write("b.md", "beta\n");
        _v.Audit.BeforeAppendTestHook = e =>
        {
            if (e.Path == "b.md" && e.Outcome == "ok")
                throw new IOException("sink died");
        };

        var result = _v.Service.Batch(
        [
            new BatchItem(BatchItemKind.Edit, "a.md", shaA, [new EditSpec("alpha", "ALPHA")]),
            new BatchItem(BatchItemKind.Edit, "b.md", shaB, [new EditSpec("beta", "BETA")]),
        ]);

        // b.md LANDED; only its audit append failed — the receipt must say
        // Applied, and the intent record plus the metric explain the gap.
        result.AllApplied.ShouldBeTrue();
        result.Items[1].Status.ShouldBe(BatchItemStatus.Applied);
        _v.ReadText("b.md").ShouldBe("BETA\n");
        _v.Metrics.Read().AuditAppendFailures.ShouldBe(1);
    }

    [Fact]
    public void Audit_dying_mid_batch_fails_the_next_item_before_its_write()
    {
        var shaA = _v.Write("a.md", "alpha\n");
        var shaB = _v.Write("b.md", "beta\n");
        var shaC = _v.Write("c.md", "gamma\n");
        _v.Audit.BeforeAppendTestHook = e =>
        {
            if (e.Path == "b.md" && e.Outcome == "attempt")
                throw new IOException("sink died before item 2's write");
        };

        var result = _v.Service.Batch(
        [
            new BatchItem(BatchItemKind.Edit, "a.md", shaA, [new EditSpec("alpha", "ALPHA")]),
            new BatchItem(BatchItemKind.Edit, "b.md", shaB, [new EditSpec("beta", "BETA")]),
            new BatchItem(BatchItemKind.Edit, "c.md", shaC, [new EditSpec("gamma", "GAMMA")]),
        ]);

        result.AllApplied.ShouldBeFalse();
        result.Items[0].Status.ShouldBe(BatchItemStatus.Applied);
        result.Items[1].Status.ShouldBe(BatchItemStatus.Failed);
        result.Items[1].ErrorCode.ShouldBe(VaultErrorCode.IoError);
        result.Items[2].Status.ShouldBe(BatchItemStatus.NotAttempted);

        _v.ReadText("a.md").ShouldBe("ALPHA\n");
        _v.ReadText("b.md").ShouldBe("beta\n"); // refused BEFORE its write
        _v.ReadText("c.md").ShouldBe("gamma\n");
    }

    [Fact]
    public void A_rejection_still_surfaces_typed_when_the_sink_is_down()
    {
        var sha = _v.Write("Notes/a.md", "original\n");
        _ = _v.Service.Edit("Notes/a.md", sha, [new EditSpec("original", "current")]);
        BreakAuditSink();

        // Stale base: the PreconditionFailed must reach the caller — the
        // audit sink's own failure must never replace the typed rejection.
        Should.Throw<KnapperException>(() =>
            _v.Service.Edit("Notes/a.md", sha, [new EditSpec("original", "other")]))
            .Code.ShouldBe(VaultErrorCode.PreconditionFailed);
    }
}
