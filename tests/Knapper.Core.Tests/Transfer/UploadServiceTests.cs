using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Knapper.Core.Mutation;
using Knapper.Core.Options;
using Knapper.Core.Tests.Mutation;
using Knapper.Core.Transfer;
using Knapper.Core.Vault;

namespace Knapper.Core.Tests.Transfer;

/// <summary>
/// The upload tools' bodies over a real mutation vault (transfer-grants §4):
/// prepare's binding and advisory checks, and commit through the shared
/// secret scan and the byte mutation paths. The PUT is stood in for by the
/// store's receive calls; the route has its own tests.
/// </summary>
public sealed class UploadServiceTests : IDisposable
{
    private static readonly TransferPrincipal Owner = new("https://team.example", "sub", "owner");
    private static readonly TransferPrincipal Other = new("https://team.example", "sub", "other");
    private readonly MutationVault _v = new();
    private readonly UploadGrantStore _store;

    public UploadServiceTests()
    {
        _store = new UploadGrantStore(new TransferOptions(), new UploadOptions { Enabled = true });
        Directory.CreateDirectory(_v.Absolute("Photos"));
    }

    public void Dispose()
    {
        _store.Dispose();
        _v.Dispose();
    }

    private UploadService Service(bool allowReplace = false, bool enabled = true, VaultMutationService? mutations = null,
        ArchivedPrefixes? archived = null) =>
        new(_v.Resolver, mutations ?? _v.Service, _store, _v.Audit, archived ?? ArchivedPrefixes.None,
            new TransferOptions { PublicOrigin = "https://vault.example.com" },
            new UploadOptions { Enabled = enabled, AllowReplace = allowReplace }, new SyncOptions());

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private static UploadPrepareRequest Create(string path, byte[] bytes) =>
        new(path, Sha(bytes), bytes.Length, "create", null);

    /// <summary>Prepare one item and stage its bytes as a PUT would; returns the upload ID.</summary>
    private string Stage(UploadService service, UploadPrepareRequest request, byte[] bytes, TransferPrincipal? who = null)
    {
        var item = service.Prepare(who ?? Owner, [request], AuditContext.None).Items.ShouldHaveSingleItem();
        var grant = item.Grant.ShouldNotBeNull($"{item.ErrorCode}: {item.ErrorMessage}");
        _store.BeginReceive(grant.UploadId, grant.UploadToken).Status.ShouldBe(UploadGrantStore.ReceiveStatus.Ok);
        _store.CompleteReceive(grant.UploadId, bytes).ShouldBeTrue();
        return grant.UploadId;
    }

    // ---- prepare ------------------------------------------------------------

    [Fact]
    public void A_grant_binds_destination_mode_and_source_and_carries_a_runnable_send_recipe()
    {
        var bytes = "hello"u8.ToArray();
        var grant = Service().Prepare(Owner, [Create("Photos/a.txt", bytes)], AuditContext.None)
            .Items.ShouldHaveSingleItem().Grant.ShouldNotBeNull();

        grant.UploadUrl.ShouldBe("https://vault.example.com/transfer/uploads/" + grant.UploadId);
        grant.UploadUrl.ShouldNotEndWith("/");
        grant.Mode.ShouldBe("create");
        grant.ExpectSha256.ShouldBeNull();
        grant.SourceSha256.ShouldBe(Sha(bytes));
        grant.ByteLength.ShouldBe(5);
        grant.Send.ShouldContain("--proto '=https'");
        grant.Send.ShouldContain("-T \"$SRC\"");
        grant.Send.ShouldNotContain(grant.UploadToken);
    }

    [Theory]
    [InlineData("create", "Photos/exists.txt", null, nameof(VaultErrorCode.AlreadyExists))]
    [InlineData("create", "Nowhere/x.txt", null, nameof(VaultErrorCode.NotFound))]
    [InlineData("create", "Photos/x.txt", "0000000000000000000000000000000000000000000000000000000000000000", nameof(VaultErrorCode.InvalidArgument))]
    [InlineData("append", "Photos/x.txt", null, nameof(VaultErrorCode.InvalidArgument))]
    [InlineData("create", "../escape.txt", null, nameof(VaultErrorCode.InvalidPath))]
    [InlineData("create", "Photos", null, nameof(VaultErrorCode.InvalidArgument))]
    public void Doomed_items_are_refused_at_prepare(string mode, string path, string? expect, string code)
    {
        _v.Write("Photos/exists.txt", "here\n");
        var item = Service().Prepare(Owner, [new UploadPrepareRequest(path, Sha([1]), 1, mode, expect)], AuditContext.None)
            .Items.ShouldHaveSingleItem();
        item.Grant.ShouldBeNull();
        item.ErrorCode.ShouldBe(code);
    }

    [Fact]
    public void Replace_is_off_unless_the_operator_allows_it_and_never_without_a_base()
    {
        var sha = _v.Write("Photos/r.txt", "base\n");
        Service(allowReplace: false).Prepare(Owner, [new UploadPrepareRequest("Photos/r.txt", Sha([1]), 1, "replace", sha)], AuditContext.None)
            .Items.Single().ErrorCode.ShouldBe(nameof(VaultErrorCode.UploadReplacementDisabled));

        var service = Service(allowReplace: true);
        service.Prepare(Owner, [new UploadPrepareRequest("Photos/r.txt", Sha([1]), 1, "replace", null)], AuditContext.None)
            .Items.Single().ErrorCode.ShouldBe(nameof(VaultErrorCode.InvalidArgument));
        service.Prepare(Owner, [new UploadPrepareRequest("Photos/missing.txt", Sha([1]), 1, "replace", sha)], AuditContext.None)
            .Items.Single().ErrorCode.ShouldBe(nameof(VaultErrorCode.NotFound));
        service.Prepare(Owner, [new UploadPrepareRequest("Photos/r.txt", Sha([1]), 1, "replace", sha)], AuditContext.None)
            .Items.Single().Grant.ShouldNotBeNull();
    }

    [Fact]
    public void Oversized_and_malformed_bindings_are_refused()
    {
        var service = Service();
        service.Prepare(Owner, [new UploadPrepareRequest("Photos/big.bin", Sha([1]), new SyncOptions().MaxFileBytes + 1, "create", null)],
            AuditContext.None).Items.Single().ErrorCode.ShouldBe(nameof(VaultErrorCode.TooLargeToSync));
        service.Prepare(Owner, [new UploadPrepareRequest("Photos/a.bin", "abc", 1, "create", null)], AuditContext.None)
            .Items.Single().ErrorCode.ShouldBe(nameof(VaultErrorCode.InvalidArgument));
        service.Prepare(Owner, [new UploadPrepareRequest("Photos/a.bin", Sha([1]), -1, "create", null)], AuditContext.None)
            .Items.Single().ErrorCode.ShouldBe(nameof(VaultErrorCode.InvalidArgument));
    }

    [Fact]
    public void Whole_call_refusals_are_typed()
    {
        var bytes = new byte[] { 1 };
        Should.Throw<KnapperException>(() => Service(enabled: false).Prepare(Owner, [Create("Photos/a", bytes)], AuditContext.None))
            .Code.ShouldBe(VaultErrorCode.UploadDisabled);
        Should.Throw<KnapperException>(() => Service().Prepare(null, [Create("Photos/a", bytes)], AuditContext.None))
            .Code.ShouldBe(VaultErrorCode.UploadNotPermitted);
        Should.Throw<KnapperException>(() => Service().Prepare(Owner, [Create("Photos/a", bytes), Create("Photos/./a", bytes)], AuditContext.None))
            .Code.ShouldBe(VaultErrorCode.InvalidArgument);
        Should.Throw<KnapperException>(() => Service().Commit(Owner, ["x", "x"], AuditContext.None))
            .Code.ShouldBe(VaultErrorCode.InvalidArgument);
    }

    [Fact]
    public void Prepare_is_audited_with_the_grant_and_never_the_token()
    {
        var bytes = "audited"u8.ToArray();
        var grant = Service().Prepare(Owner, [Create("Photos/a.txt", bytes)], new AuditContext("dan", "r1"))
            .Items.Single().Grant!;

        var line = JsonDocument.Parse(_v.AuditLines().Single()).RootElement;
        line.GetProperty("Op").GetString().ShouldBe("upload_prepare");
        line.GetProperty("GrantId").GetString().ShouldBe(grant.UploadId);
        line.GetProperty("AfterSha256").GetString().ShouldBe(Sha(bytes));
        line.GetProperty("Principal").GetString().ShouldBe("sub:owner@https://team.example");
        string.Join("\n", _v.AuditLines()).ShouldNotContain(grant.UploadToken);
    }

    [Fact]
    public void A_grant_whose_audit_cannot_be_written_is_withdrawn()
    {
        _v.Audit.BeforeAppendTestHook = _ => throw new UnauthorizedAccessException("audit file mode changed");
        var item = Service().Prepare(Owner, [Create("Photos/a.txt", [1])], AuditContext.None).Items.Single();
        item.Grant.ShouldBeNull();
        item.ErrorCode.ShouldBe(nameof(VaultErrorCode.IoError));
        _store.TrackedCount.ShouldBe(0);
    }

    // ---- commit -------------------------------------------------------------

    [Fact]
    public void A_staged_create_lands_exact_bytes_and_the_receipt_hash_is_the_source_hash()
    {
        byte[] bytes = [0xFF, 0xD8, 0x00, 0x0D, 0x0A, 0x80];
        var service = Service();
        var id = Stage(service, Create("Photos/pic.jpg", bytes), bytes);

        var item = service.Commit(Owner, [id], new AuditContext("dan", "r2")).Items.Single();

        item.State.ShouldBe("succeeded");
        item.Result.ShouldNotBeNull().NewSha256.ShouldBe(Sha(bytes));
        File.ReadAllBytes(_v.Absolute("Photos/pic.jpg")).ShouldBe(bytes);
        _v.AuditLines().Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("Op").GetString() == "create")
            .ShouldAllBe(e => e.GetProperty("GrantId").GetString() == id);
    }

    [Fact]
    public void A_staged_replace_needs_its_base_to_still_be_current()
    {
        var sha = _v.Write("Photos/r.txt", "base\n");
        var service = Service(allowReplace: true);
        var bytes = "replacement\n"u8.ToArray();
        var id = Stage(service, new UploadPrepareRequest("Photos/r.txt", Sha(bytes), bytes.Length, "replace", sha), bytes);

        _v.Write("Photos/r.txt", "someone else\n"); // the base moved after prepare

        var item = service.Commit(Owner, [id], AuditContext.None).Items.Single();
        item.State.ShouldBe("failed");
        item.ErrorCode.ShouldBe(nameof(VaultErrorCode.PreconditionFailed));
        _v.ReadText("Photos/r.txt").ShouldBe("someone else\n");
        // Terminal, never retried: a second commit returns the cached failure.
        service.Commit(Owner, [id], AuditContext.None).Items.Single().ErrorCode.ShouldBe(nameof(VaultErrorCode.PreconditionFailed));
    }

    [Fact]
    public void A_staged_replace_lands_when_the_base_is_current()
    {
        var sha = _v.Write("Photos/r.txt", "base\n");
        var service = Service(allowReplace: true);
        byte[] bytes = [0xFE, 0xFF, 1];
        var id = Stage(service, new UploadPrepareRequest("Photos/r.txt", Sha(bytes), bytes.Length, "replace", sha), bytes);

        var item = service.Commit(Owner, [id], AuditContext.None).Items.Single();
        item.State.ShouldBe("succeeded");
        item.Result!.OldSha256.ShouldBe(sha);
        File.ReadAllBytes(_v.Absolute("Photos/r.txt")).ShouldBe(bytes);
    }

    /// <summary>
    /// The pre-landing scan (§4.3): a credential the committer would refuse
    /// never reaches the vault, and the finding is masked everywhere.
    /// </summary>
    [Fact]
    public void A_secret_in_the_payload_is_refused_before_any_byte_lands()
    {
        var secret = "AKIAIOSFODNN7EXAMPLE";
        var bytes = Encoding.Latin1.GetBytes($"café\nAWS={secret}\n"); // not strict UTF-8: still scanned
        var service = Service();
        var id = Stage(service, Create("Photos/creds.env", bytes), bytes);

        var item = service.Commit(Owner, [id], AuditContext.None).Items.Single();

        item.State.ShouldBe("failed");
        item.ErrorCode.ShouldBe(nameof(VaultErrorCode.SecretDetected));
        item.ErrorMessage.ShouldNotBeNull().ShouldNotContain(secret);
        File.Exists(_v.Absolute("Photos/creds.env")).ShouldBeFalse();
        _v.TempFiles().ShouldBeEmpty();
        string.Join("\n", _v.AuditLines()).ShouldNotContain(secret);
        _v.AuditLines().ShouldContain(l => l.Contains("\"upload_commit\"") && l.Contains("\"SecretDetected\""));
    }

    [Fact]
    public void Items_commit_independently_and_one_failure_never_blocks_the_rest()
    {
        var service = Service();
        _v.Write("Photos/taken.bin", "x");
        var good = Stage(service, Create("Photos/b-good.bin", [1]), [1]);
        var clash = Stage(service, Create("Photos/a-clash.bin", [2]), [2]);
        File.WriteAllBytes(_v.Absolute("Photos/a-clash.bin"), [9]); // appeared after prepare

        var items = service.Commit(Owner, [good, clash], AuditContext.None).Items;

        items.Select(i => i.UploadId).ShouldBe([good, clash]); // answered in the caller's order
        items[0].State.ShouldBe("succeeded");
        items[1].ErrorCode.ShouldBe(nameof(VaultErrorCode.AlreadyExists));
        File.ReadAllBytes(_v.Absolute("Photos/a-clash.bin")).ShouldBe(new byte[] { 9 });
    }

    [Fact]
    public void Commit_refuses_what_it_cannot_commit_and_consumes_nothing()
    {
        var service = Service();
        var grant = service.Prepare(Owner, [Create("Photos/pending.bin", [1])], AuditContext.None).Items.Single().Grant!;

        var notReady = service.Commit(Owner, [grant.UploadId], AuditContext.None).Items.Single();
        notReady.ErrorCode.ShouldBe(nameof(VaultErrorCode.UploadNotReady));
        notReady.State.ShouldBe("issued");

        service.Commit(Other, [grant.UploadId], AuditContext.None).Items.Single()
            .ErrorCode.ShouldBe(nameof(VaultErrorCode.UploadNotFound));
        service.Commit(Owner, ["nope"], AuditContext.None).Items.Single().State.ShouldBe("unknown");

        _store.BeginReceive(grant.UploadId, grant.UploadToken);
        _store.CompleteReceive(grant.UploadId, [1]);
        service.Commit(Owner, [grant.UploadId], AuditContext.None).Items.Single().State.ShouldBe("succeeded");
    }

    [Fact]
    public void Status_reports_each_state_and_hides_other_principals_grants()
    {
        var service = Service();
        var id = Stage(service, Create("Photos/s.bin", [1]), [1]);

        var ready = service.Status(Owner, [id]).Items.Single();
        ready.State.ShouldBe("ready");
        ready.Path.ShouldBe("Photos/s.bin");
        ready.ByteLength.ShouldBe(1);

        var hidden = service.Status(Other, [id]).Items.Single();
        hidden.State.ShouldBe("unknown");
        hidden.Path.ShouldBeNull();
        hidden.ErrorCode.ShouldBe(nameof(VaultErrorCode.UploadNotFound));

        service.Commit(Owner, [id], AuditContext.None);
        service.Status(Owner, [id]).Items.Single().Result.ShouldNotBeNull();
    }

    [Fact]
    public void An_archived_replace_is_refused_and_an_archived_create_lands()
    {
        _v.Write("Archive/old.txt", "superseded\n");
        var service = Service(allowReplace: true, mutations: _v.ServiceWithArchived("Archive"),
            archived: new ArchivedPrefixes(["Archive"]));
        service.Prepare(Owner, [new UploadPrepareRequest("Archive/old.txt", Sha([1]), 1, "replace", Sha("superseded\n"u8.ToArray()))],
            AuditContext.None).Items.Single().ErrorCode.ShouldBe(nameof(VaultErrorCode.PathArchived));

        var id = Stage(service, Create("Archive/filed.bin", [7]), [7]);
        service.Commit(Owner, [id], AuditContext.None).Items.Single().State.ShouldBe("succeeded");
    }
}
