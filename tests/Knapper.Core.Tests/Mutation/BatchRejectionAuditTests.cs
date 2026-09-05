using Knapper.Core.Mutation;
using Knapper.Core.Options;

namespace Knapper.Core.Tests.Mutation;

/// <summary>
/// Batch-WIDE rejections must land in the audit log, like every other
/// post-resolution rejection. The single-item operations audit their gate,
/// lock, and precondition failures through an outer catch; Batch's gate
/// passes, duplicate-path refusal, and lock timeout used to throw without any
/// entry — a batch could be refused for a stale gate answer and the audit
/// trail would show nothing happened at all. One entry lands per resolved
/// path (the rejection refused every one of them), paths and counts only.
/// </summary>
public sealed class BatchRejectionAuditTests : IDisposable
{
    private readonly MutationVault _v = new();

    public void Dispose() => _v.Dispose();

    private static BatchItem Edit(string path, string sha) =>
        new(BatchItemKind.Edit, path, sha, [new EditSpec("base", "changed")]);

    [Fact]
    public void A_gate_rejection_audits_every_path_in_the_batch()
    {
        var shaA = _v.Write("a.md", "base\n");
        var shaB = _v.Write("b.md", "base\n");

        Should.Throw<KnapperException>(() => _v.BlockedService.Batch([Edit("a.md", shaA), Edit("b.md", shaB)]))
            .Code.ShouldBe(VaultErrorCode.MutationBlocked);

        var lines = _v.AuditLines();
        lines.ShouldContain(l => l.Contains("\"a.md\"") && l.Contains("\"MutationBlocked\""));
        lines.ShouldContain(l => l.Contains("\"b.md\"") && l.Contains("\"MutationBlocked\""));
    }

    [Fact]
    public void A_duplicate_path_rejection_is_audited()
    {
        var sha = _v.Write("a.md", "base\n");

        Should.Throw<KnapperException>(() => _v.Service.Batch([Edit("a.md", sha), Edit("a.md", sha)]))
            .Code.ShouldBe(VaultErrorCode.InvalidArgument);

        _v.AuditLines().ShouldContain(l => l.Contains("\"a.md\"") && l.Contains("\"InvalidArgument\""));
    }

    [Fact]
    public void A_lock_timeout_is_audited()
    {
        var sha = _v.Write("a.md", "base\n");
        var impatient = new VaultMutationService(
            _v.Resolver, _v.Locks, _v.Generation, _v.Conflicts, StaticSyncGate.Open,
            new VaultOptions
            {
                RootPath = _v.Options.RootPath,
                LockDirectory = _v.Options.LockDirectory,
                AuditLogPath = _v.Options.AuditLogPath,
                LockTimeoutMs = 150,
            },
            _v.SyncOptions, Knapper.Core.Vault.ArchivedPrefixes.None, _v.Audit);

        using (_v.Locks.AcquirePathLock(_v.Resolver.Resolve("a.md"), TimeSpan.FromSeconds(5)))
        {
            Should.Throw<KnapperException>(() => impatient.Batch([Edit("a.md", sha)]))
                .Code.ShouldBe(VaultErrorCode.LockTimeout);
        }

        _v.AuditLines().ShouldContain(l =>
            l.Contains("\"a.md\"") && l.Contains("\"LockTimeout\"") && l.Contains("batch of 1 rejected"));
    }

    /// <summary>
    /// The boundary must not double-audit: a per-ITEM validation failure
    /// already lands its own `batch-validate` entry and must not also produce
    /// batch-wide rejection entries for every path.
    /// </summary>
    [Fact]
    public void A_per_item_validation_failure_is_audited_once_not_batch_wide()
    {
        var shaA = _v.Write("a.md", "base\n");
        _v.Write("b.md", "base\n");

        Should.Throw<KnapperException>(() =>
            _v.Service.Batch([Edit("a.md", shaA), Edit("b.md", "0000000000000000000000000000000000000000000000000000000000000000")]))
            .Code.ShouldBe(VaultErrorCode.PreconditionFailed);

        var lines = _v.AuditLines();
        lines.ShouldContain(l => l.Contains("batch-validate") && l.Contains("\"b.md\""));
        lines.ShouldNotContain(l => l.Contains("batch of 2 rejected"),
            "an item's own failure is not a batch-wide gate/lock rejection");
    }
}
