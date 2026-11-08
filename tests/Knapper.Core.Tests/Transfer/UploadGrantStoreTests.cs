using Knapper.Core.Mutation;
using Knapper.Core.Options;
using Knapper.Core.Transfer;

namespace Knapper.Core.Tests.Transfer;

/// <summary>The upload state machine (transfer-grants §7) and its share of the one transfer budget (§10).</summary>
public class UploadGrantStoreTests
{
    private static readonly TransferPrincipal Alice = new("https://team.example", "sub", "alice");
    private static readonly TransferPrincipal Bob = new("https://team.example", "sub", "bob");

    private static UploadBinding Binding(long length = 3, string path = "Photos/a.bin") =>
        new(path, UploadBinding.Create, null, new string('a', 64), length);

    private static (UploadGrantStore Store, ManualClock Clock) NewStore(
        long budget = 1024, int active = 4, int perPrincipal = 4, int receivers = 2, int tracked = 32,
        int lifetime = 600, int receive = 60, int retention = 900, TransferBudget? shared = null)
    {
        var clock = new ManualClock();
        var transfer = new TransferOptions { MaxBufferedBytes = budget, MaxTrackedGrants = tracked };
        var store = new UploadGrantStore(transfer, new UploadOptions
        {
            Enabled = true, MaxActiveGrants = active, MaxActiveGrantsPerPrincipal = perPrincipal,
            MaxConcurrentReceives = receivers, GrantLifetimeSeconds = lifetime, ReceiveTimeoutSeconds = receive,
            ReceiptRetentionSeconds = retention,
        }, clock, shared);
        return (store, clock);
    }

    private static MutationResult Receipt() => new("Photos/a.bin", null, new string('a', 64), 0, 3, true, 1, []);

    [Fact]
    public void The_happy_path_walks_every_state_and_frees_the_bytes()
    {
        var (store, _) = NewStore();
        var issued = store.Issue(Alice, Binding());
        store.BufferedBytes.ShouldBe(3);
        store.Status(Alice, issued.UploadId)!.State.ShouldBe(UploadState.Issued);

        store.BeginReceive(issued.UploadId, issued.Token).Status.ShouldBe(UploadGrantStore.ReceiveStatus.Ok);
        store.Status(Alice, issued.UploadId)!.State.ShouldBe(UploadState.Receiving);
        store.CompleteReceive(issued.UploadId, [1, 2, 3]).ShouldBeTrue();
        store.Status(Alice, issued.UploadId)!.State.ShouldBe(UploadState.Ready);

        var claim = store.BeginCommit(Alice, issued.UploadId);
        claim.Status.ShouldBe(UploadGrantStore.CommitStatus.Ok);
        claim.Bytes.ShouldBe(new byte[] { 1, 2, 3 });
        store.Status(Alice, issued.UploadId)!.State.ShouldBe(UploadState.Committing);

        store.FinishCommit(issued.UploadId, Receipt(), null, null);
        var done = store.Status(Alice, issued.UploadId)!;
        done.State.ShouldBe(UploadState.Succeeded);
        done.Result.ShouldNotBeNull();
        store.BufferedBytes.ShouldBe(0);
    }

    [Fact]
    public void A_wrong_token_never_burns_the_grant_and_one_put_claims_it()
    {
        var (store, _) = NewStore();
        var issued = store.Issue(Alice, Binding());
        var other = store.Issue(Alice, Binding(path: "Photos/b.bin"));

        store.BeginReceive(issued.UploadId, other.Token).Status.ShouldBe(UploadGrantStore.ReceiveStatus.InvalidToken);
        store.BeginReceive(issued.UploadId, null).Status.ShouldBe(UploadGrantStore.ReceiveStatus.InvalidToken);
        store.BeginReceive(issued.Token, issued.Token).Status.ShouldBe(UploadGrantStore.ReceiveStatus.InvalidToken);

        store.BeginReceive(issued.UploadId, issued.Token).Status.ShouldBe(UploadGrantStore.ReceiveStatus.Ok);
        store.BeginReceive(issued.UploadId, issued.Token).Status.ShouldBe(UploadGrantStore.ReceiveStatus.AlreadyClaimed);
    }

    [Fact]
    public void Concurrent_puts_admit_exactly_one_receiver()
    {
        var (store, _) = NewStore(receivers: 4);
        var issued = store.Issue(Alice, Binding());
        var results = new UploadGrantStore.ReceiveStatus[32];
        Parallel.For(0, results.Length, i => results[i] = store.BeginReceive(issued.UploadId, issued.Token).Status);
        results.Count(r => r == UploadGrantStore.ReceiveStatus.Ok).ShouldBe(1);
    }

    [Fact]
    public void A_full_receiver_pool_answers_busy_and_claims_nothing()
    {
        var (store, _) = NewStore(receivers: 1);
        var first = store.Issue(Alice, Binding(path: "Photos/1.bin"));
        var second = store.Issue(Alice, Binding(path: "Photos/2.bin"));
        store.BeginReceive(first.UploadId, first.Token).Status.ShouldBe(UploadGrantStore.ReceiveStatus.Ok);

        store.BeginReceive(second.UploadId, second.Token).Status.ShouldBe(UploadGrantStore.ReceiveStatus.Busy);
        store.Status(Alice, second.UploadId)!.State.ShouldBe(UploadState.Issued);

        store.CompleteReceive(first.UploadId, [1, 2, 3]);
        store.BeginReceive(second.UploadId, second.Token).Status.ShouldBe(UploadGrantStore.ReceiveStatus.Ok);
    }

    [Fact]
    public void A_failed_receive_is_terminal_and_frees_the_bytes()
    {
        var (store, _) = NewStore();
        var issued = store.Issue(Alice, Binding());
        store.BeginReceive(issued.UploadId, issued.Token);
        store.FailReceive(issued.UploadId, VaultErrorCode.UploadHashMismatch, "changed");

        var view = store.Status(Alice, issued.UploadId)!;
        view.State.ShouldBe(UploadState.Failed);
        view.ErrorCode.ShouldBe(nameof(VaultErrorCode.UploadHashMismatch));
        store.BufferedBytes.ShouldBe(0);
        store.BeginCommit(Alice, issued.UploadId).Status.ShouldBe(UploadGrantStore.CommitStatus.Terminal);
    }

    [Fact]
    public void Commit_is_for_the_preparing_principal_only_and_not_before_the_bytes_arrive()
    {
        var (store, _) = NewStore();
        var issued = store.Issue(Alice, Binding());

        store.BeginCommit(Bob, issued.UploadId).Status.ShouldBe(UploadGrantStore.CommitStatus.NotFound);
        store.Status(Bob, issued.UploadId).ShouldBeNull();
        store.BeginCommit(Alice, issued.UploadId).Status.ShouldBe(UploadGrantStore.CommitStatus.NotReady);
        store.BeginCommit(Alice, "no-such-id").Status.ShouldBe(UploadGrantStore.CommitStatus.NotFound);

        // NotReady consumed nothing: the transfer and commit still work.
        store.BeginReceive(issued.UploadId, issued.Token);
        store.CompleteReceive(issued.UploadId, [1, 2, 3]);
        store.BeginCommit(Alice, issued.UploadId).Status.ShouldBe(UploadGrantStore.CommitStatus.Ok);
    }

    [Fact]
    public void A_grant_commits_at_most_once()
    {
        var (store, _) = NewStore();
        var issued = store.Issue(Alice, Binding());
        store.BeginReceive(issued.UploadId, issued.Token);
        store.CompleteReceive(issued.UploadId, [1, 2, 3]);

        store.BeginCommit(Alice, issued.UploadId).Status.ShouldBe(UploadGrantStore.CommitStatus.Ok);
        var duplicate = store.BeginCommit(Alice, issued.UploadId);
        duplicate.Status.ShouldBe(UploadGrantStore.CommitStatus.Committing);
        duplicate.Bytes.ShouldBeNull();

        store.FinishCommit(issued.UploadId, null, "PreconditionFailed", "stale");
        var after = store.BeginCommit(Alice, issued.UploadId);
        after.Status.ShouldBe(UploadGrantStore.CommitStatus.Terminal);
        after.Grant!.ErrorCode.ShouldBe("PreconditionFailed");
    }

    [Fact]
    public void Expiry_stops_a_start_but_never_a_commit_in_flight()
    {
        var (store, clock) = NewStore(lifetime: 60);
        var unused = store.Issue(Alice, Binding(path: "Photos/unused.bin"));
        var staged = store.Issue(Alice, Binding(path: "Photos/staged.bin"));
        var inFlight = store.Issue(Alice, Binding(path: "Photos/flight.bin"));
        foreach (var g in new[] { staged, inFlight })
        {
            store.BeginReceive(g.UploadId, g.Token);
            store.CompleteReceive(g.UploadId, [1, 2, 3]);
        }
        store.BeginCommit(Alice, inFlight.UploadId).Status.ShouldBe(UploadGrantStore.CommitStatus.Ok);

        clock.Advance(TimeSpan.FromSeconds(61));

        store.BeginReceive(unused.UploadId, unused.Token).Status.ShouldBe(UploadGrantStore.ReceiveStatus.Expired);
        store.BeginCommit(Alice, staged.UploadId).Status.ShouldBe(UploadGrantStore.CommitStatus.Expired);
        store.Status(Alice, inFlight.UploadId)!.State.ShouldBe(UploadState.Committing);
        store.FinishCommit(inFlight.UploadId, Receipt(), null, null);
        store.Status(Alice, inFlight.UploadId)!.State.ShouldBe(UploadState.Succeeded);
        store.BufferedBytes.ShouldBe(0);
    }

    [Fact]
    public void A_receive_past_its_deadline_fails_and_a_late_completion_is_discarded()
    {
        var (store, clock) = NewStore(receive: 10);
        var issued = store.Issue(Alice, Binding());
        store.BeginReceive(issued.UploadId, issued.Token);

        clock.Advance(TimeSpan.FromSeconds(11));
        store.Sweep();

        store.Status(Alice, issued.UploadId)!.ErrorCode.ShouldBe(nameof(VaultErrorCode.UploadTransferTimeout));
        store.CompleteReceive(issued.UploadId, [1, 2, 3]).ShouldBeFalse();
        store.BufferedBytes.ShouldBe(0);
    }

    [Fact]
    public void Terminal_records_are_retained_then_forgotten()
    {
        var (store, clock) = NewStore(retention: 60);
        var issued = store.Issue(Alice, Binding());
        store.BeginReceive(issued.UploadId, issued.Token);
        store.FailReceive(issued.UploadId, VaultErrorCode.UploadSizeMismatch, "short");

        clock.Advance(TimeSpan.FromSeconds(59));
        store.Status(Alice, issued.UploadId).ShouldNotBeNull();
        clock.Advance(TimeSpan.FromSeconds(2));
        store.Status(Alice, issued.UploadId).ShouldBeNull();
        store.TrackedCount.ShouldBe(0);
    }

    [Fact]
    public void Slot_and_byte_limits_refuse_without_issuing()
    {
        var (store, _) = NewStore(budget: 5, active: 3, perPrincipal: 2);
        store.Issue(Alice, Binding(length: 2, path: "Photos/1.bin"));
        Should.Throw<KnapperException>(() => store.Issue(Bob, Binding(length: 4, path: "Photos/2.bin")))
            .Code.ShouldBe(VaultErrorCode.UploadCapacityExceeded);
        store.Issue(Alice, Binding(length: 1, path: "Photos/3.bin"));
        Should.Throw<KnapperException>(() => store.Issue(Alice, Binding(length: 0, path: "Photos/4.bin")))
            .Code.ShouldBe(VaultErrorCode.UploadCapacityExceeded);
        store.BufferedBytes.ShouldBe(3);
    }

    /// <summary>
    /// ONE budget across both directions (§10): download snapshots and upload
    /// buffers together never exceed Transfer:MaxBufferedBytes or the record cap.
    /// </summary>
    [Fact]
    public void Uploads_and_downloads_draw_on_one_budget()
    {
        var transfer = new TransferOptions { MaxBufferedBytes = 10, MaxTrackedGrants = 3 };
        var shared = new TransferBudget(transfer);
        using var downloads = new DownloadGrantStore(transfer, new DownloadOptions(), budget: shared);
        var (uploads, _) = NewStore(budget: 10, tracked: 3, shared: shared);

        downloads.Issue(Alice, new byte[6]);
        Should.Throw<KnapperException>(() => uploads.Issue(Alice, Binding(length: 5)))
            .Code.ShouldBe(VaultErrorCode.UploadCapacityExceeded);
        uploads.Issue(Alice, Binding(length: 4));
        shared.Bytes.ShouldBe(10);

        downloads.Issue(Alice, []);
        Should.Throw<KnapperException>(() => uploads.Issue(Alice, Binding(length: 0, path: "Photos/z.bin")))
            .Code.ShouldBe(VaultErrorCode.UploadCapacityExceeded); // the record cap, shared too
    }

    [Fact]
    public void A_revoked_grant_returns_its_slot_and_bytes()
    {
        var (store, _) = NewStore();
        var issued = store.Issue(Alice, Binding(length: 7));
        store.Revoke(issued.UploadId);
        store.BufferedBytes.ShouldBe(0);
        store.TrackedCount.ShouldBe(0);
        store.BeginReceive(issued.UploadId, issued.Token).Status.ShouldBe(UploadGrantStore.ReceiveStatus.InvalidToken);
    }

    /// <summary>
    /// Review 2026-11-08: the receive deadline fails the grant, but the slot
    /// and bytes stay counted until the handler — possibly still inside its
    /// read, buffer live — ends its lease. Releasing at the sweep let a
    /// second receiver in past MaxConcurrentReceives and over the byte budget.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_swept_receive_keeps_its_slot_and_bytes_until_the_handler_lets_go(bool handlerCompletes)
    {
        var (store, clock) = NewStore(budget: 6, receivers: 1, receive: 10);
        var first = store.Issue(Alice, Binding(length: 3, path: "Photos/1.bin"));
        var second = store.Issue(Alice, Binding(length: 3, path: "Photos/2.bin"));
        store.BeginReceive(first.UploadId, first.Token).Status.ShouldBe(UploadGrantStore.ReceiveStatus.Ok);

        clock.Advance(TimeSpan.FromSeconds(11));
        store.Sweep();

        store.Status(Alice, first.UploadId)!.ErrorCode.ShouldBe(nameof(VaultErrorCode.UploadTransferTimeout));
        store.BeginReceive(second.UploadId, second.Token).Status.ShouldBe(UploadGrantStore.ReceiveStatus.Busy);
        store.BufferedBytes.ShouldBe(6);

        if (handlerCompletes)
            store.CompleteReceive(first.UploadId, [1, 2, 3]).ShouldBeFalse();
        else
            store.FailReceive(first.UploadId, VaultErrorCode.UploadTransferFailed, "late");

        store.Status(Alice, first.UploadId)!.ErrorCode.ShouldBe(nameof(VaultErrorCode.UploadTransferTimeout)); // never re-labelled
        store.BufferedBytes.ShouldBe(3);
        store.BeginReceive(second.UploadId, second.Token).Status.ShouldBe(UploadGrantStore.ReceiveStatus.Ok);

        // Releasing twice is a no-op: the pool cannot be driven negative.
        store.FailReceive(first.UploadId, VaultErrorCode.UploadTransferFailed, "again");
        var third = store.Issue(Alice, Binding(length: 0, path: "Photos/3.bin"));
        store.BeginReceive(third.UploadId, third.Token).Status.ShouldBe(UploadGrantStore.ReceiveStatus.Busy);
    }
}
