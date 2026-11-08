using Knapper.Core.Options;
using Knapper.Core.Transfer;

namespace Knapper.Core.Tests.Transfer;

/// <summary>A clock the test moves by hand. Monotonic time and wall time advance together.</summary>
internal sealed class ManualClock : TimeProvider
{
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _ticks;
    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero).AddTicks(_ticks);
    public void Advance(TimeSpan by) => _ticks += by.Ticks;
}

public class DownloadGrantStoreTests
{
    private static readonly TransferPrincipal Alice = new("https://team.example", "sub", "alice");
    private static readonly TransferPrincipal Bob = new("https://team.example", "sub", "bob");

    private static (DownloadGrantStore Store, ManualClock Clock) NewStore(
        long budget = 1024, int active = 4, int perPrincipal = 4, int tracked = 32, int lifetime = 300)
    {
        var clock = new ManualClock();
        var store = new DownloadGrantStore(
            new TransferOptions { MaxBufferedBytes = budget, MaxTrackedGrants = tracked },
            new DownloadOptions { MaxActiveGrants = active, MaxActiveGrantsPerPrincipal = perPrincipal, GrantLifetimeSeconds = lifetime },
            clock);
        return (store, clock);
    }

    [Fact]
    public void A_grant_serves_its_snapshot_exactly_once()
    {
        var (store, _) = NewStore();
        var issued = store.Issue(Alice, [1, 2, 3]);

        var first = store.TryClaim(issued.DownloadId, issued.Token);
        first.Status.ShouldBe(DownloadGrantStore.ClaimStatus.Ok);
        first.Bytes.ShouldBe(new byte[] { 1, 2, 3 });
        first.Release();

        store.TryClaim(issued.DownloadId, issued.Token).Status.ShouldBe(DownloadGrantStore.ClaimStatus.AlreadyClaimed);
    }

    [Fact]
    public void A_wrong_token_is_refused_without_burning_the_grant()
    {
        var (store, _) = NewStore();
        var issued = store.Issue(Alice, [9]);
        var other = store.Issue(Alice, [8]);

        store.TryClaim(issued.DownloadId, other.Token).Status.ShouldBe(DownloadGrantStore.ClaimStatus.InvalidToken);
        store.TryClaim(issued.DownloadId, null).Status.ShouldBe(DownloadGrantStore.ClaimStatus.InvalidToken);
        store.TryClaim(issued.DownloadId, "not-a-token").Status.ShouldBe(DownloadGrantStore.ClaimStatus.InvalidToken);
        store.TryClaim(issued.DownloadId, issued.Token + "A").Status.ShouldBe(DownloadGrantStore.ClaimStatus.InvalidToken);

        store.TryClaim(issued.DownloadId, issued.Token).Status.ShouldBe(DownloadGrantStore.ClaimStatus.Ok);
    }

    [Fact]
    public void An_unknown_id_looks_exactly_like_a_bad_token()
    {
        var (store, _) = NewStore();
        var issued = store.Issue(Alice, [1]);
        store.TryClaim(issued.Token, issued.Token).Status.ShouldBe(DownloadGrantStore.ClaimStatus.InvalidToken);
        store.TryClaim(null, issued.Token).Status.ShouldBe(DownloadGrantStore.ClaimStatus.InvalidToken);
    }

    [Fact]
    public void An_expired_grant_answers_expired_frees_its_bytes_and_is_later_forgotten()
    {
        var (store, clock) = NewStore(lifetime: 60);
        var issued = store.Issue(Alice, new byte[100]);
        store.BufferedBytes.ShouldBe(100);

        clock.Advance(TimeSpan.FromSeconds(61));
        store.TryClaim(issued.DownloadId, issued.Token).Status.ShouldBe(DownloadGrantStore.ClaimStatus.Expired);
        store.BufferedBytes.ShouldBe(0);

        clock.Advance(TimeSpan.FromSeconds(61));
        store.TryClaim(issued.DownloadId, issued.Token).Status.ShouldBe(DownloadGrantStore.ClaimStatus.InvalidToken);
        store.TrackedCount.ShouldBe(0);
    }

    [Fact]
    public void Bytes_stay_counted_until_the_served_claim_is_released()
    {
        var (store, _) = NewStore(budget: 10);
        var issued = store.Issue(Alice, new byte[10]);
        var claim = store.TryClaim(issued.DownloadId, issued.Token);
        store.BufferedBytes.ShouldBe(10);
        Should.Throw<KnapperException>(() => store.Issue(Alice, new byte[1]))
            .Code.ShouldBe(VaultErrorCode.DownloadCapacityExceeded);

        claim.Release();
        claim.Release(); // idempotent: freeing twice would let the budget go negative
        store.BufferedBytes.ShouldBe(0);
        store.Issue(Alice, new byte[10]).ShouldNotBeNull();
    }

    [Fact]
    public void The_byte_budget_refuses_without_issuing()
    {
        var (store, _) = NewStore(budget: 10);
        store.Issue(Alice, new byte[6]);
        var refused = Should.Throw<KnapperException>(() => store.Issue(Alice, new byte[5]));
        refused.Code.ShouldBe(VaultErrorCode.DownloadCapacityExceeded);
        store.BufferedBytes.ShouldBe(6);
        store.TrackedCount.ShouldBe(1);
    }

    [Fact]
    public void Per_principal_and_process_wide_slot_limits_both_hold()
    {
        var (store, _) = NewStore(active: 3, perPrincipal: 2);
        store.Issue(Alice, [1]);
        store.Issue(Alice, [1]);
        Should.Throw<KnapperException>(() => store.Issue(Alice, [1])).Code.ShouldBe(VaultErrorCode.DownloadCapacityExceeded);
        store.Issue(Bob, [1]);
        Should.Throw<KnapperException>(() => store.Issue(Bob, [1])).Code.ShouldBe(VaultErrorCode.DownloadCapacityExceeded);
    }

    [Fact]
    public void A_revoked_grant_frees_its_bytes_and_cannot_be_claimed()
    {
        var (store, _) = NewStore();
        var issued = store.Issue(Alice, new byte[7]);
        store.Revoke(issued.DownloadId);
        store.BufferedBytes.ShouldBe(0);
        store.TryClaim(issued.DownloadId, issued.Token).Status.ShouldBe(DownloadGrantStore.ClaimStatus.InvalidToken);
    }

    [Fact]
    public void Concurrent_claims_admit_exactly_one()
    {
        var (store, _) = NewStore();
        var issued = store.Issue(Alice, new byte[64]);
        var results = new DownloadGrantStore.ClaimStatus[32];
        Parallel.For(0, results.Length, i => results[i] = store.TryClaim(issued.DownloadId, issued.Token).Status);
        results.Count(r => r == DownloadGrantStore.ClaimStatus.Ok).ShouldBe(1);
        results.Count(r => r == DownloadGrantStore.ClaimStatus.AlreadyClaimed).ShouldBe(31);
    }

    [Fact]
    public void Ids_and_tokens_are_independent_43_character_base64url_values()
    {
        var (store, _) = NewStore();
        var issued = store.Issue(Alice, [1]);
        issued.DownloadId.Length.ShouldBe(43);
        issued.Token.Length.ShouldBe(43);
        issued.DownloadId.ShouldNotBe(issued.Token);
        (issued.DownloadId + issued.Token).ShouldAllBe(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');
    }

    [Fact]
    public void A_reservation_holds_bytes_and_a_slot_before_any_snapshot_exists()
    {
        var (store, _) = NewStore(budget: 10, active: 2);
        using var first = store.Reserve(Alice, 6);
        store.BufferedBytes.ShouldBe(6);
        Should.Throw<KnapperException>(() => store.Reserve(Alice, 5)).Code.ShouldBe(VaultErrorCode.DownloadCapacityExceeded);

        using var second = store.Reserve(Bob, 4);
        Should.Throw<KnapperException>(() => store.Reserve(Bob, 0)).Code.ShouldBe(VaultErrorCode.DownloadCapacityExceeded);
    }

    [Fact]
    public void Releasing_an_unconsumed_reservation_frees_it_and_consuming_one_keeps_the_bytes()
    {
        var (store, _) = NewStore(budget: 10);
        var released = store.Reserve(Alice, 7);
        released.Dispose();
        released.Dispose();
        store.BufferedBytes.ShouldBe(0);

        var consumed = store.Reserve(Alice, 3);
        store.Issue(consumed, new byte[3], "p", "s");
        consumed.Dispose(); // no-op once consumed: the grant owns the bytes now
        store.BufferedBytes.ShouldBe(3);
        Should.Throw<InvalidOperationException>(() => store.Issue(consumed, new byte[3], "p", "s"));
    }

    [Fact]
    public void A_snapshot_must_be_exactly_the_reserved_size()
    {
        var (store, _) = NewStore();
        using var reservation = store.Reserve(Alice, 4);
        Should.Throw<InvalidOperationException>(() => store.Issue(reservation, new byte[5], "p", "s"));
    }

    [Fact]
    public void Concurrent_reservations_never_exceed_the_budget()
    {
        var (store, _) = NewStore(budget: 5 * 100, active: 64, perPrincipal: 64, tracked: 128);
        var held = new System.Collections.Concurrent.ConcurrentBag<DownloadGrantStore.Reservation>();
        var refused = 0;
        Parallel.For(0, 40, _ =>
        {
            try
            {
                held.Add(store.Reserve(Alice, 100));
            }
            catch (KnapperException)
            {
                Interlocked.Increment(ref refused);
            }
        });
        held.Count.ShouldBe(5);
        refused.ShouldBe(35);
        store.BufferedBytes.ShouldBe(500);
    }

    [Fact]
    public void A_claim_names_the_grant_a_valid_token_proved()
    {
        var (store, _) = NewStore();
        var issued = store.Issue(Alice, [1], "Photos/a.jpg", "abc");
        var claim = store.TryClaim(issued.DownloadId, issued.Token);
        claim.Path.ShouldBe("Photos/a.jpg");
        claim.Sha256.ShouldBe("abc");
        claim.Principal.ShouldBe(Alice);
        claim.DownloadId.ShouldBe(issued.DownloadId);
        store.TryClaim(issued.DownloadId, issued.Token).Path.ShouldBe("Photos/a.jpg"); // AlreadyClaimed still names it
        store.TryClaim(issued.DownloadId, "x").Path.ShouldBeNull(); // an invalid token proves nothing
    }
}
