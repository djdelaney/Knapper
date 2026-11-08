using System.Text.Json;
using Knapper.Core.Mutation;
using Knapper.Core.Options;
using Knapper.Core.Transfer;
using Knapper.Core.Vault;

namespace Knapper.Core.Tests.Transfer;

public class DownloadServiceTests : IDisposable
{
    private static readonly TransferPrincipal Owner = new("https://team.example", "sub", "owner");
    private readonly TempDir _vault = new();
    private readonly TempDir _outside = new();
    private readonly AuditLog _audit;
    private readonly DownloadGrantStore _store;
    private readonly DownloadService _service;

    public DownloadServiceTests()
    {
        _audit = new AuditLog(Path.Combine(_outside.Path, "audit.jsonl"));
        var transfer = new TransferOptions { PublicOrigin = "https://vault.example.com" };
        var download = new DownloadOptions { Enabled = true, AllowedPrefixes = ["Photos"] };
        _store = new DownloadGrantStore(transfer, download);
        _service = new DownloadService(
            new DownloadSnapshotReader(new VaultPathResolver(_vault.Path), new SyncOptions(), download),
            _store, _audit, transfer, download);
    }

    public void Dispose()
    {
        _store.Dispose();
        _vault.Dispose();
        _outside.Dispose();
    }

    private string AuditText() => File.ReadAllText(Path.Combine(_outside.Path, "audit.jsonl"));

    [Fact]
    public void A_grant_is_audited_with_its_id_hash_and_principal_and_never_its_content_or_token()
    {
        _vault.File("Photos/secret-plan.txt", "CONTENT-MARKER");

        var item = _service.Prepare(Owner, [new DownloadPrepareRequest("Photos/secret-plan.txt", null)], new AuditContext("dan", "req-1"))
            .Items.ShouldHaveSingleItem();

        var grant = item.Grant.ShouldNotBeNull();
        grant.DownloadUrl.ShouldBe("https://vault.example.com/transfer/downloads/" + grant.DownloadId);
        grant.Fetch.ShouldContain("--proto '=https'");
        grant.Fetch.ShouldNotContain(grant.DownloadToken);

        var line = JsonDocument.Parse(AuditText().Trim()).RootElement;
        line.GetProperty("Op").GetString().ShouldBe("download_prepare");
        line.GetProperty("Outcome").GetString().ShouldBe("ok");
        line.GetProperty("GrantId").GetString().ShouldBe(grant.DownloadId);
        line.GetProperty("AfterSha256").GetString().ShouldBe(grant.Sha256);
        line.GetProperty("Principal").GetString().ShouldBe("sub:owner@https://team.example");
        line.GetProperty("ByteLength").GetInt64().ShouldBe(14);
        AuditText().ShouldNotContain("CONTENT-MARKER");
        AuditText().ShouldNotContain(grant.DownloadToken);
    }

    [Fact]
    public void A_grant_whose_audit_cannot_be_written_is_withdrawn()
    {
        _vault.File("Photos/a.txt", "x");
        _audit.BeforeAppendTestHook = _ => throw new IOException("disk full");

        var item = _service.Prepare(Owner, [new DownloadPrepareRequest("Photos/a.txt", null)], AuditContext.None)
            .Items.ShouldHaveSingleItem();

        item.Grant.ShouldBeNull();
        item.ErrorCode.ShouldBe(nameof(VaultErrorCode.IoError));
        _store.TrackedCount.ShouldBe(0);
        _store.BufferedBytes.ShouldBe(0);
    }

    [Fact]
    public void One_bad_item_never_hides_the_rest_and_refusals_are_audited_by_code()
    {
        _vault.File("Photos/ok.txt", "ok");
        _vault.File("Private/no.txt", "no");

        var items = _service.Prepare(Owner,
            [
                new DownloadPrepareRequest("Photos/ok.txt", null),
                new DownloadPrepareRequest("Private/no.txt", null),
                new DownloadPrepareRequest("../escape", null),
            ], AuditContext.None).Items;

        items[0].Grant.ShouldNotBeNull();
        items[1].ErrorCode.ShouldBe(nameof(VaultErrorCode.DownloadNotPermitted));
        items[2].ErrorCode.ShouldBe(nameof(VaultErrorCode.InvalidPath));
        AuditText().ShouldContain("\"Outcome\":\"DownloadNotPermitted\"");
        AuditText().ShouldNotContain("escape"); // never resolved: no vault object to audit
    }

    [Fact]
    public void Whole_call_refusals_are_typed()
    {
        Should.Throw<KnapperException>(() => _service.Prepare(null, [new DownloadPrepareRequest("Photos/a", null)], AuditContext.None))
            .Code.ShouldBe(VaultErrorCode.DownloadNotPermitted);
        Should.Throw<KnapperException>(() => _service.Prepare(Owner, [], AuditContext.None))
            .Code.ShouldBe(VaultErrorCode.InvalidArgument);
        Should.Throw<KnapperException>(() => _service.Prepare(Owner,
                [.. Enumerable.Range(0, 17).Select(i => new DownloadPrepareRequest($"Photos/{i}", null))], AuditContext.None))
            .Code.ShouldBe(VaultErrorCode.InvalidArgument);

        var off = new DownloadOptions { Enabled = false };
        var disabled = new DownloadService(
            new DownloadSnapshotReader(new VaultPathResolver(_vault.Path), new SyncOptions(), off),
            _store, _audit, new TransferOptions(), off);
        Should.Throw<KnapperException>(() => disabled.Prepare(Owner, [new DownloadPrepareRequest("Photos/a", null)], AuditContext.None))
            .Code.ShouldBe(VaultErrorCode.DownloadDisabled);
    }

    /// <summary>
    /// Review 2026-11-08: an audit append that fails with a PERMISSION error
    /// (UnauthorizedAccessException is not an IOException) on a refused item
    /// escaped the best-effort handler and discarded the whole response,
    /// stranding the earlier item's grant.
    /// </summary>
    [Fact]
    public void A_permission_failure_on_a_refusals_audit_keeps_every_items_result()
    {
        _vault.File("Photos/ok.txt", "ok");
        _vault.File("Private/no.txt", "no");
        _audit.BeforeAppendTestHook = e =>
        {
            if (e.Outcome != "ok")
                throw new UnauthorizedAccessException("audit file mode changed");
        };

        var items = _service.Prepare(Owner,
            [new DownloadPrepareRequest("Photos/ok.txt", null), new DownloadPrepareRequest("Private/no.txt", null)],
            AuditContext.None).Items;

        items[0].Grant.ShouldNotBeNull();
        items[1].ErrorCode.ShouldBe(nameof(VaultErrorCode.DownloadNotPermitted));
        _store.TrackedCount.ShouldBe(1);
    }

    [Fact]
    public void A_full_budget_refuses_an_item_and_holds_nothing_for_it()
    {
        var transfer = new TransferOptions { PublicOrigin = "https://vault.example.com", MaxBufferedBytes = 4 };
        var download = new DownloadOptions { Enabled = true, AllowedPrefixes = ["Photos"] };
        using var store = new DownloadGrantStore(transfer, download);
        var service = new DownloadService(
            new DownloadSnapshotReader(new VaultPathResolver(_vault.Path), new SyncOptions(), download),
            store, _audit, transfer, download);
        _vault.File("Photos/small.txt", "abc");
        _vault.File("Photos/big.txt", "abcdef");

        var items = service.Prepare(Owner,
            [new DownloadPrepareRequest("Photos/small.txt", null), new DownloadPrepareRequest("Photos/big.txt", null)],
            AuditContext.None).Items;

        items[0].Grant.ShouldNotBeNull();
        items[1].ErrorCode.ShouldBe(nameof(VaultErrorCode.DownloadCapacityExceeded));
        store.BufferedBytes.ShouldBe(3);
    }
}
