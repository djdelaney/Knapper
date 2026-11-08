using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Knapper.Core.Mutation;
using Knapper.Core.Options;
using Knapper.Core.Tests.Mutation;
using Knapper.Core.Transfer;
using Knapper.Core.Vault;

namespace Knapper.Core.Tests.Transfer;

/// <summary>
/// Review 2026-11-08: a batch commit kept every item's staged buffer alive
/// until the whole batch finished, while each item's capacity went back to
/// the budget as soon as it committed — so memory the budget called free was
/// still live while a later item waited. Each buffer must be collectable
/// before the next item's write begins.
/// </summary>
[Collection("AtomicFile static test hooks")]
public sealed class UploadBufferLifetimeTests : IDisposable
{
    private static readonly TransferPrincipal Owner = new("https://team.example", "sub", "owner");
    private readonly MutationVault _v = new();
    private readonly UploadGrantStore _store = new(new TransferOptions(), new UploadOptions { Enabled = true });

    public void Dispose()
    {
        AtomicFile.BeforeCreateLinkTestHook = null;
        _store.Dispose();
        _v.Dispose();
    }

    private UploadService Service() =>
        new(_v.Resolver, _v.Service, _store, _v.Audit, ArchivedPrefixes.None,
            new TransferOptions { PublicOrigin = "https://vault.example.com" },
            new UploadOptions { Enabled = true }, new SyncOptions());

    /// <summary>Stage a fresh buffer and keep only a weak reference to it.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private (string Id, WeakReference Buffer) Stage(UploadService service, string path, int size)
    {
        var bytes = RandomNumberGenerator.GetBytes(size);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var grant = service.Prepare(Owner, [new UploadPrepareRequest(path, sha, size, "create", null)], AuditContext.None)
            .Items.Single().Grant!;
        _store.BeginReceive(grant.UploadId, grant.UploadToken);
        _store.CompleteReceive(grant.UploadId, bytes);
        return (grant.UploadId, new WeakReference(bytes));
    }

    [Fact]
    public void A_committed_items_buffer_is_released_before_the_next_item_writes()
    {
        Directory.CreateDirectory(_v.Absolute("Photos"));
        var service = Service();
        var (firstId, firstBuffer) = Stage(service, "Photos/a.bin", 1 << 20);
        var (secondId, _) = Stage(service, "Photos/b.bin", 1 << 20);

        bool? firstAliveDuringSecond = null;
        var secondTarget = _v.Resolver.Resolve("Photos/b.bin").Absolute;
        AtomicFile.BeforeCreateLinkTestHook = path =>
        {
            if (path != secondTarget)
                return;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            firstAliveDuringSecond = firstBuffer.IsAlive;
        };

        var items = service.Commit(Owner, [secondId, firstId], AuditContext.None).Items;

        items.ShouldAllBe(i => i.State == "succeeded");
        firstAliveDuringSecond.ShouldBe(false, "the first item's buffer must be collectable once it committed");
    }
}
