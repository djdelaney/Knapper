using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Knapper.Core.Mutation;
using Knapper.Core.Options;
using Knapper.Core.Vault;

namespace Knapper.Core.Tests.Mutation;

/// <summary>
/// The byte-oriented mutation paths uploads commit through
/// (transfer-grants §12): <see cref="VaultMutationService.CreateBytes"/>,
/// which the text create now delegates to, and the CONDITIONAL
/// <see cref="VaultMutationService.ReplaceBytes"/>. Every rule the text paths
/// keep must hold here too — a byte path that skipped one would let an upload
/// bypass it with no error and no other test noticing.
/// </summary>
public sealed class ByteMutationTests : IDisposable
{
    private readonly MutationVault _v = new();

    public void Dispose() => _v.Dispose();

    private static readonly byte[] Awkward =
        [0xEF, 0xBB, 0xBF, 0x61, 0x0D, 0x0A, 0xFF, 0xFE, 0x00, 0x80, 0x0A];

    private string WriteBytes(string relative, byte[] bytes)
    {
        var path = _v.VaultDir.File(relative);
        File.WriteAllBytes(path, bytes);
        return VaultHash.Sha256Hex(bytes);
    }

    // ---- create ----------------------------------------------------------

    [Fact]
    public void Create_lands_exact_bytes_and_the_receipt_hash_is_the_source_hash()
    {
        Directory.CreateDirectory(_v.Absolute("Photos"));

        var receipt = _v.Service.CreateBytes("Photos/raw.bin", Awkward);

        File.ReadAllBytes(_v.Absolute("Photos/raw.bin")).ShouldBe(Awkward);
        receipt.NewSha256.ShouldBe(VaultHash.Sha256Hex(Awkward));
        receipt.OldSha256.ShouldBeNull();
        receipt.Verified.ShouldBeTrue();
        receipt.BytesAfter.ShouldBe(Awkward.Length);
        _v.TempFiles().ShouldBeEmpty();
    }

    [Fact]
    public void Create_of_zero_bytes_is_a_file()
    {
        Directory.CreateDirectory(_v.Absolute("Photos"));
        _v.Service.CreateBytes("Photos/empty.bin", []).NewSha256.ShouldBe(VaultHash.Sha256Hex([]));
        new FileInfo(_v.Absolute("Photos/empty.bin")).Length.ShouldBe(0);
    }

    [Fact]
    public void Create_never_clobbers_and_needs_its_parent()
    {
        var sha = _v.Write("Notes/here.md", "original\n");
        Should.Throw<KnapperException>(() => _v.Service.CreateBytes("Notes/here.md", [1, 2]))
            .Code.ShouldBe(VaultErrorCode.AlreadyExists);
        VaultHash.Sha256Hex(File.ReadAllBytes(_v.Absolute("Notes/here.md"))).ShouldBe(sha);

        Should.Throw<KnapperException>(() => _v.Service.CreateBytes("Nowhere/x.bin", [1]))
            .Code.ShouldBe(VaultErrorCode.NotFound);
    }

    [Fact]
    public void The_text_create_is_the_byte_create_with_utf8()
    {
        Directory.CreateDirectory(_v.Absolute("Notes"));
        var receipt = _v.Service.Create("Notes/t.md", "héllo\r\n");
        File.ReadAllBytes(_v.Absolute("Notes/t.md")).ShouldBe(Encoding.UTF8.GetBytes("héllo\r\n"));
        receipt.NewSha256.ShouldBe(VaultHash.Sha256Hex(Encoding.UTF8.GetBytes("héllo\r\n")));
    }

    // ---- replace ---------------------------------------------------------

    [Fact]
    public void Replace_swaps_whole_content_including_non_utf8_on_both_sides()
    {
        var sha = WriteBytes("Photos/pic.jpg", [0xFF, 0xD8, 0xFF, 0x00]);

        var receipt = _v.Service.ReplaceBytes("Photos/pic.jpg", sha, Awkward);

        File.ReadAllBytes(_v.Absolute("Photos/pic.jpg")).ShouldBe(Awkward);
        receipt.OldSha256.ShouldBe(sha);
        receipt.NewSha256.ShouldBe(VaultHash.Sha256Hex(Awkward));
        receipt.Verified.ShouldBeTrue();
        _v.TempFiles().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Replace_is_never_unconditional(string sha)
    {
        WriteBytes("Photos/pic.jpg", [1, 2, 3]);
        Should.Throw<KnapperException>(() => _v.Service.ReplaceBytes("Photos/pic.jpg", sha, [9]))
            .Code.ShouldBe(VaultErrorCode.InvalidArgument);
        File.ReadAllBytes(_v.Absolute("Photos/pic.jpg")).ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public void A_stale_base_rejects_without_mutating()
    {
        WriteBytes("Photos/pic.jpg", [1, 2, 3]);
        var stale = VaultHash.Sha256Hex([0]);
        Should.Throw<KnapperException>(() => _v.Service.ReplaceBytes("Photos/pic.jpg", stale, [9]))
            .Code.ShouldBe(VaultErrorCode.PreconditionFailed);
        File.ReadAllBytes(_v.Absolute("Photos/pic.jpg")).ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public void Replace_refuses_a_missing_file_rather_than_creating_one()
    {
        Directory.CreateDirectory(_v.Absolute("Photos"));
        Should.Throw<KnapperException>(() => _v.Service.ReplaceBytes("Photos/none.jpg", VaultHash.Sha256Hex([]), [1]))
            .Code.ShouldBe(VaultErrorCode.NotFound);
        File.Exists(_v.Absolute("Photos/none.jpg")).ShouldBeFalse();
    }

    [Fact]
    public void Replace_keeps_the_existing_files_mode()
    {
        var sha = WriteBytes("Scripts/run.sh", "#!/bin/sh\necho 1\n"u8.ToArray());
        File.SetUnixFileMode(_v.Absolute("Scripts/run.sh"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);

        _v.Service.ReplaceBytes("Scripts/run.sh", sha, "#!/bin/sh\necho 2\n"u8.ToArray());

        File.GetUnixFileMode(_v.Absolute("Scripts/run.sh"))
            .ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);
    }

    // ---- vault rules on the byte path -----------------------------------

    [Fact]
    public void An_archived_replace_is_refused_and_an_archived_create_is_allowed()
    {
        var sha = _v.Write("Archive/old.md", "superseded\n");
        var service = _v.ServiceWithArchived("Archive");

        Should.Throw<KnapperException>(() => service.ReplaceBytes("Archive/old.md", sha, "new\n"u8.ToArray()))
            .Code.ShouldBe(VaultErrorCode.PathArchived);
        _v.ReadText("Archive/old.md").ShouldBe("superseded\n");

        service.CreateBytes("Archive/filed.bin", [1, 2, 3]).Verified.ShouldBeTrue();
    }

    [Fact]
    public void The_sync_ceiling_applies_to_both_byte_paths()
    {
        var sha = WriteBytes("Photos/pic.jpg", new byte[10]);
        var service = _v.ServiceWithMaxFileBytes(16);

        Should.Throw<KnapperException>(() => service.CreateBytes("Photos/big.bin", new byte[17]))
            .Code.ShouldBe(VaultErrorCode.TooLargeToSync);
        Should.Throw<KnapperException>(() => service.ReplaceBytes("Photos/pic.jpg", sha, new byte[17]))
            .Code.ShouldBe(VaultErrorCode.TooLargeToSync);
        File.Exists(_v.Absolute("Photos/big.bin")).ShouldBeFalse();

        service.CreateBytes("Photos/edge.bin", new byte[16]).Verified.ShouldBeTrue(); // inclusive, like Sync
    }

    [Fact]
    public void A_markdown_payload_gets_the_same_convention_warnings_as_text_and_binary_gets_none()
    {
        Directory.CreateDirectory(_v.Absolute("Notes"));
        var service = _v.ServiceWithConventions(new ConventionsOptions { WikilinksOnly = true });
        var markdownLink = "See [other](Other.md).\n"u8.ToArray();

        var viaText = service.Create("Notes/a.md", "See [other](Other.md).\n");
        var viaBytes = service.CreateBytes("Notes/b.md", markdownLink);
        viaBytes.Warnings.Select(w => w.Rule).ShouldBe(viaText.Warnings.Select(w => w.Rule));
        viaBytes.Warnings.ShouldNotBeEmpty();

        service.CreateBytes("Notes/c.md", [.. markdownLink, 0xFF]).Warnings.ShouldBeEmpty(); // not UTF-8: unjudged
        service.CreateBytes("Notes/d.bin", markdownLink).Warnings.ShouldBeEmpty();           // not Markdown

        var sha = VaultHash.Sha256Hex(File.ReadAllBytes(_v.Absolute("Notes/b.md")));
        service.ReplaceBytes("Notes/b.md", sha, [.. markdownLink, .. "And [x](y.md).\n"u8.ToArray()])
            .Warnings.ShouldNotBeEmpty(); // judged on what the replace ADDED
    }

    [Fact]
    public void A_conflict_sibling_blocks_a_replace()
    {
        var sha = _v.Write("Notes/doc.md", "base\n");
        _v.Write("Notes/doc (Conflicted copy 2026-11-08).md", "theirs\n");
        Should.Throw<KnapperException>(() => _v.Service.ReplaceBytes("Notes/doc.md", sha, "mine\n"u8.ToArray()))
            .Code.ShouldBe(VaultErrorCode.MutationBlocked);
        _v.ReadText("Notes/doc.md").ShouldBe("base\n");
    }

    [Fact]
    public void Unhealthy_sync_blocks_both_byte_paths()
    {
        var sha = _v.Write("Notes/doc.md", "base\n");
        Should.Throw<KnapperException>(() => _v.BlockedService.ReplaceBytes("Notes/doc.md", sha, [1]))
            .Code.ShouldBe(VaultErrorCode.MutationBlocked);
        Should.Throw<KnapperException>(() => _v.BlockedService.CreateBytes("Notes/new.bin", [1]))
            .Code.ShouldBe(VaultErrorCode.MutationBlocked);
    }

    [Fact]
    public void A_grants_id_and_principal_ride_on_every_record_of_the_mutation()
    {
        var sha = WriteBytes("Photos/pic.jpg", [1]);
        var ctx = new AuditContext("dan", "req-9", GrantId: "grant-abc", Principal: "sub:u1@iss");

        _v.Service.ReplaceBytes("Photos/pic.jpg", sha, [2], ctx);
        Should.Throw<KnapperException>(() => _v.Service.ReplaceBytes("Photos/pic.jpg", sha, [3], ctx));

        var lines = _v.AuditLines().Select(l => JsonDocument.Parse(l).RootElement).ToList();
        lines.Select(e => e.GetProperty("Outcome").GetString()).ShouldBe(["attempt", "ok", "PreconditionFailed"]);
        lines.ShouldAllBe(e => e.GetProperty("Op").GetString() == "replace");
        lines.ShouldAllBe(e => e.GetProperty("GrantId").GetString() == "grant-abc");
        lines.ShouldAllBe(e => e.GetProperty("Principal").GetString() == "sub:u1@iss");
    }

    [Fact]
    public void An_ordinary_call_writes_no_grant_fields()
    {
        Directory.CreateDirectory(_v.Absolute("Notes"));
        _v.Service.Create("Notes/plain.md", "x\n", new AuditContext("dan", "r"));
        _v.AuditLines().ShouldAllBe(l => !l.Contains("GrantId") && !l.Contains("Principal"));
    }

    [Fact]
    public void A_dead_audit_sink_refuses_both_byte_paths_before_any_byte_lands()
    {
        var sha = WriteBytes("Photos/pic.jpg", [1]);
        Directory.CreateDirectory(_v.Absolute("Photos"));
        _v.Audit.BeforeAppendTestHook = e =>
        {
            if (e.Outcome == "attempt")
                throw new IOException("audit disk full");
        };

        Should.Throw<KnapperException>(() => _v.Service.ReplaceBytes("Photos/pic.jpg", sha, [2]))
            .Code.ShouldBe(VaultErrorCode.IoError);
        Should.Throw<KnapperException>(() => _v.Service.CreateBytes("Photos/new.bin", [2]))
            .Code.ShouldBe(VaultErrorCode.IoError);

        File.ReadAllBytes(_v.Absolute("Photos/pic.jpg")).ShouldBe(new byte[] { 1 });
        File.Exists(_v.Absolute("Photos/new.bin")).ShouldBeFalse();
        _v.TempFiles().ShouldBeEmpty();
    }

    // ---- real second processes -------------------------------------------

    private Process Probe(params string[] probeArgs)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Knapper.MutationProbe.dll");
        File.Exists(dll).ShouldBeTrue($"probe not found at {dll}");
        var psi = new ProcessStartInfo { FileName = "dotnet", RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(dll);
        foreach (var a in probeArgs)
            psi.ArgumentList.Add(a);
        return Process.Start(psi)!;
    }

    private static (bool Ok, string? Code) Await(Process probe)
    {
        var stdout = probe.StandardOutput.ReadToEnd();
        var stderr = probe.StandardError.ReadToEnd();
        probe.WaitForExit(30_000).ShouldBeTrue("probe never exited");
        var line = stdout.Trim().Split('\n').LastOrDefault();
        line.ShouldNotBeNullOrEmpty($"probe produced no result; stderr: {stderr}");
        var json = JsonDocument.Parse(line!).RootElement;
        probe.Dispose();
        return (json.GetProperty("ok").GetBoolean(), json.GetProperty("code").GetString());
    }

    [Fact]
    public void Concurrent_replaces_from_one_base_across_processes_have_exactly_one_winner()
    {
        var sha = WriteBytes("Photos/shared.bin", [0]);
        var payloads = Enumerable.Range(1, 4).Select(i => new byte[] { (byte)i, 0xFF, (byte)i }).ToList();

        var results = payloads
            .Select(p => Probe("replace", _v.VaultDir.Path, _v.Options.LockDirectory, "Photos/shared.bin", sha,
                Convert.ToBase64String(p)))
            .ToList()
            .Select(Await)
            .ToList();

        results.Count(r => r.Ok).ShouldBe(1);
        results.Where(r => !r.Ok).ShouldAllBe(r => r.Code == nameof(VaultErrorCode.PreconditionFailed));
        var landed = File.ReadAllBytes(_v.Absolute("Photos/shared.bin"));
        payloads.ShouldContain(p => p.SequenceEqual(landed));
        _v.TempFiles().ShouldBeEmpty();
    }

    [Fact]
    public void Concurrent_creates_of_one_path_across_processes_never_clobber()
    {
        Directory.CreateDirectory(_v.Absolute("Photos"));
        var payloads = Enumerable.Range(1, 4).Select(i => new byte[] { (byte)i, 0x00, 0xFE }).ToList();

        var results = payloads
            .Select(p => Probe("createbytes", _v.VaultDir.Path, _v.Options.LockDirectory, "Photos/once.bin",
                Convert.ToBase64String(p)))
            .ToList()
            .Select(Await)
            .ToList();

        results.Count(r => r.Ok).ShouldBe(1);
        results.Where(r => !r.Ok).ShouldAllBe(r => r.Code == nameof(VaultErrorCode.AlreadyExists));
        payloads.ShouldContain(p => p.SequenceEqual(File.ReadAllBytes(_v.Absolute("Photos/once.bin"))));
    }

    [Fact]
    public void A_replace_landed_by_another_process_stales_our_base()
    {
        var sha = WriteBytes("Photos/pic.jpg", [1]);
        Await(Probe("replace", _v.VaultDir.Path, _v.Options.LockDirectory, "Photos/pic.jpg", sha,
            Convert.ToBase64String(new byte[] { 2 }))).Ok.ShouldBeTrue();

        Should.Throw<KnapperException>(() => _v.Service.ReplaceBytes("Photos/pic.jpg", sha, [3]))
            .Code.ShouldBe(VaultErrorCode.PreconditionFailed);
        File.ReadAllBytes(_v.Absolute("Photos/pic.jpg")).ShouldBe(new byte[] { 2 });
    }
}
