using System.Text;
using Knapper.Core.Options;
using Knapper.Core.Query;
using Knapper.Core.Vault;

namespace Knapper.Core.Tests.Query;

public sealed class VaultReadServiceTests : IClassFixture<FixtureVault>
{
    private readonly FixtureVault _vault;

    public VaultReadServiceTests(FixtureVault vault) => _vault = vault;

    [Fact]
    public void Whole_read_returns_content_and_the_whole_file_sha()
    {
        var result = _vault.Reader.Read("Notes/Daily.md");
        result.Content.ShouldBe("# Daily\nTODO alpha task\ntodo beta task\nDone gamma\nwrap TODO up\n");
        result.TotalLines.ShouldBe(5);
        result.Encoding.ShouldBe("utf-8");
        result.Sha256.ShouldBe(VaultHash.Sha256Hex(Encoding.UTF8.GetBytes(result.Content)));
        result.RangeStart.ShouldBeNull();
    }

    [Fact]
    public void Ranged_read_returns_lines_and_still_the_whole_file_sha()
    {
        var whole = _vault.Reader.Read("Notes/Daily.md");
        var range = _vault.Reader.Read("Notes/Daily.md", 2, 3);
        range.Content.ShouldBe("TODO alpha task\ntodo beta task");
        range.RangeStart.ShouldBe(2);
        range.RangeEnd.ShouldBe(3);
        range.Sha256.ShouldBe(whole.Sha256); // precondition currency: always whole-file
        range.TotalLines.ShouldBe(5);
    }

    [Fact]
    public void Range_end_clamps_explicitly_but_bad_starts_reject()
    {
        var clamped = _vault.Reader.Read("Notes/Daily.md", 4, 99);
        clamped.RangeEnd.ShouldBe(5); // echoed truth, not silent

        Should.Throw<KnapperException>(() => _vault.Reader.Read("Notes/Daily.md", 6, 8))
            .Code.ShouldBe(VaultErrorCode.InvalidArgument);
        Should.Throw<KnapperException>(() => _vault.Reader.Read("Notes/Daily.md", 0, 2))
            .Code.ShouldBe(VaultErrorCode.InvalidArgument);
        Should.Throw<KnapperException>(() => _vault.Reader.Read("Notes/Daily.md", 3, 2))
            .Code.ShouldBe(VaultErrorCode.InvalidArgument);
    }

    [Fact]
    public void Empty_file_reads_as_zero_lines()
    {
        var result = _vault.Reader.Read("empty.md");
        result.Content.ShouldBe("");
        result.TotalLines.ShouldBe(0);
    }

    [Fact]
    public void Non_utf8_is_a_typed_refusal_and_binary_likewise()
    {
        Should.Throw<KnapperException>(() => _vault.Reader.Read("latin1/legacy.md"))
            .Code.ShouldBe(VaultErrorCode.NotUtf8);
        Should.Throw<KnapperException>(() => _vault.Reader.Read("raw/blob.bin"))
            .Code.ShouldBe(VaultErrorCode.NotUtf8);
    }

    [Fact]
    public void Bom_is_recognized_stripped_and_reported()
    {
        _vault.Dir.File("bom.md");
        File.WriteAllBytes(Path.Combine(_vault.Dir.Path, "bom.md"), [0xEF, 0xBB, 0xBF, .. "hello\n"u8]);
        try
        {
            var result = _vault.Reader.Read("bom.md");
            result.Encoding.ShouldBe("utf-8-bom");
            result.Content.ShouldBe("hello\n");
        }
        finally
        {
            File.Delete(Path.Combine(_vault.Dir.Path, "bom.md"));
        }
    }

    [Fact]
    public void Missing_file_and_directory_are_typed()
    {
        Should.Throw<KnapperException>(() => _vault.Reader.Read("ghost.md"))
            .Code.ShouldBe(VaultErrorCode.NotFound);
        Should.Throw<KnapperException>(() => _vault.Reader.Read("Notes"))
            .Code.ShouldBe(VaultErrorCode.InvalidArgument);
    }

    [Fact]
    public void Oversize_file_is_an_explicit_TooLarge_never_a_truncated_read()
    {
        var tiny = new VaultReadService(_vault.Resolver, new VaultOptions { MaxReadBytes = 10 }, _vault.Generation);
        Should.Throw<KnapperException>(() => tiny.Read("Notes/Daily.md"))
            .Code.ShouldBe(VaultErrorCode.TooLarge);
        // Ranged reads don't bypass the cap either.
        Should.Throw<KnapperException>(() => tiny.Read("Notes/Daily.md", 1, 1))
            .Code.ShouldBe(VaultErrorCode.TooLarge);
    }

    [Fact]
    public void Batch_read_isolates_failures_per_item()
    {
        var results = _vault.Reader.BatchRead(
        [
            new VaultReadRequest("Notes/Daily.md", 1, 2),
            new VaultReadRequest("ghost.md"),
            new VaultReadRequest("raw/blob.bin"),
            new VaultReadRequest("empty.md"),
        ]);

        results.Items.Count.ShouldBe(4);
        results.Items[0].Result.ShouldNotBeNull().Content.ShouldBe("# Daily\nTODO alpha task");
        results.Items[1].ErrorCode.ShouldBe(VaultErrorCode.NotFound);
        results.Items[2].ErrorCode.ShouldBe(VaultErrorCode.NotUtf8);
        results.Items[3].Result.ShouldNotBeNull();
        results.GenerationEnd.ShouldBeGreaterThanOrEqualTo(results.GenerationStart);
    }

    [Fact]
    public void Read_and_stat_carry_the_generation_span()
    {
        // Freshness signal only — the SHA stays the precondition. The span
        // must track the live counter, not a constructor-time snapshot.
        var read = _vault.Reader.Read("Notes/Daily.md");
        read.GenerationStart.ShouldBe(_vault.Generation.Current);
        read.GenerationEnd.ShouldBe(read.GenerationStart);
        read.ChangedDuringRead.ShouldBeFalse();

        _vault.Generation.Increment();
        var stat = _vault.Reader.Stat("Notes/Daily.md");
        stat.GenerationStart.ShouldBe(_vault.Generation.Current);
        stat.GenerationEnd.ShouldBe(_vault.Generation.Current);
        stat.ChangedDuringRead.ShouldBeFalse();
    }

    [Fact]
    public void Batch_caps_are_typed()
    {
        Should.Throw<KnapperException>(() => _vault.Reader.BatchRead([]))
            .Code.ShouldBe(VaultErrorCode.InvalidArgument);
        var many = Enumerable.Range(0, 51).Select(_ => new VaultReadRequest("empty.md")).ToList();
        Should.Throw<KnapperException>(() => _vault.Reader.BatchRead(many))
            .Code.ShouldBe(VaultErrorCode.InvalidArgument);
    }

    [Fact]
    public void Stat_reports_without_a_body()
    {
        var file = _vault.Reader.Stat("Notes/Daily.md");
        file.Exists.ShouldBeTrue();
        file.IsDirectory.ShouldBeFalse();
        file.IsText.ShouldBe(true);
        file.Encoding.ShouldBe("utf-8");
        file.TotalLines.ShouldBe(5);
        file.Sha256.ShouldBe(_vault.Reader.Read("Notes/Daily.md").Sha256);

        _vault.Reader.Stat("Notes").IsDirectory.ShouldBeTrue();
        _vault.Reader.Stat("ghost.md").Exists.ShouldBeFalse();

        var binary = _vault.Reader.Stat("raw/blob.bin");
        binary.IsText.ShouldBe(false);
        binary.Encoding.ShouldBe("binary");
        binary.Sha256.ShouldNotBeNull(); // stat still hashes binaries — moves need preconditions too
    }

    [Fact]
    public void Stat_streams_the_hash_past_the_read_cap()
    {
        // The cap bounds the BODY; the SHA is the mutation precondition and
        // must exist for files vault_read refuses as TooLarge.
        var capped = new VaultReadService(_vault.Resolver, new VaultOptions { MaxReadBytes = 10 }, _vault.Generation);

        var text = capped.Stat("Notes/Daily.md");
        text.Sha256.ShouldBe(_vault.Reader.Stat("Notes/Daily.md").Sha256);
        text.Encoding.ShouldBe("utf-8");
        text.IsText.ShouldBe(true);
        text.TotalLines.ShouldBeNull(); // counting would need a full decode

        var binary = capped.Stat("raw/blob.bin");
        binary.Sha256.ShouldBe(_vault.Reader.Stat("raw/blob.bin").Sha256);
        binary.Encoding.ShouldBe("binary");
        binary.IsText.ShouldBe(false);

        // vault_read keeps its explicit refusal.
        Should.Throw<KnapperException>(() => capped.Read("Notes/Daily.md"))
            .Code.ShouldBe(VaultErrorCode.TooLarge);
    }
}
