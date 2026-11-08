using System.Diagnostics;
using Knapper.Core.Options;
using Knapper.Core.Transfer;
using Knapper.Core.Vault;

namespace Knapper.Core.Tests.Transfer;

public class DownloadSnapshotReaderTests : IDisposable
{
    private readonly TempDir _vault = new();

    public void Dispose() => _vault.Dispose();

    private DownloadSnapshotReader Reader(string[] allowed, long maxFileBytes = 5 * 1024 * 1024) =>
        new(new VaultPathResolver(_vault.Path), new SyncOptions { MaxFileBytes = maxFileBytes },
            new DownloadOptions { AllowedPrefixes = allowed });

    [Fact]
    public void Serves_exact_bytes_with_their_hash_text_or_binary()
    {
        byte[] bytes = [0xFF, 0xFE, 0x00, 0x0D, 0x0A, 0xEF, 0xBB, 0xBF, 0x80];
        File.WriteAllBytes(_vault.File("Photos/a.bin"), bytes);

        var snapshot = Reader(["Photos"]).Read("Photos/a.bin", null);

        snapshot.Path.ShouldBe("Photos/a.bin");
        snapshot.Bytes.ShouldBe(bytes);
        snapshot.Sha256.ShouldBe(VaultHash.Sha256Hex(bytes));
    }

    [Fact]
    public void An_empty_allowlist_serves_nothing()
    {
        _vault.File("Photos/a.jpg", "x");
        Should.Throw<KnapperException>(() => Reader([]).Read("Photos/a.jpg", null))
            .Code.ShouldBe(VaultErrorCode.DownloadNotPermitted);
    }

    [Fact]
    public void The_allowlist_is_boundary_aware_and_case_exact()
    {
        _vault.File("Photos Old/a.jpg", "x");
        _vault.File("photos/b.jpg", "x");
        _vault.File("Photos/Sub/c.jpg", "x");
        var reader = Reader(["Photos"]);

        Should.Throw<KnapperException>(() => reader.Read("Photos Old/a.jpg", null)).Code.ShouldBe(VaultErrorCode.DownloadNotPermitted);
        Should.Throw<KnapperException>(() => reader.Read("photos/b.jpg", null)).Code.ShouldBe(VaultErrorCode.DownloadNotPermitted);
        reader.Read("Photos/Sub/c.jpg", null).Path.ShouldBe("Photos/Sub/c.jpg");
    }

    [Fact]
    public void A_stale_expectation_issues_nothing()
    {
        _vault.File("Photos/a.txt", "now");
        var stale = VaultHash.Sha256Hex("then"u8);
        Should.Throw<KnapperException>(() => Reader(["Photos"]).Read("Photos/a.txt", stale))
            .Code.ShouldBe(VaultErrorCode.PreconditionFailed);
        Reader(["Photos"]).Read("Photos/a.txt", VaultHash.Sha256Hex("now"u8).ToUpperInvariant()).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData(" 0000000000000000000000000000000000000000000000000000000000000000")]
    public void A_malformed_expectation_is_an_argument_error(string sha)
    {
        _vault.File("Photos/a.txt", "x");
        Should.Throw<KnapperException>(() => Reader(["Photos"]).Read("Photos/a.txt", sha))
            .Code.ShouldBe(VaultErrorCode.InvalidArgument);
    }

    [Fact]
    public void Files_over_the_sync_ceiling_are_refused()
    {
        File.WriteAllBytes(_vault.File("Photos/big.bin"), new byte[101]);
        Should.Throw<KnapperException>(() => Reader(["Photos"], maxFileBytes: 100).Read("Photos/big.bin", null))
            .Code.ShouldBe(VaultErrorCode.TooLarge);
        File.WriteAllBytes(_vault.File("Photos/edge.bin"), new byte[100]);
        Reader(["Photos"], maxFileBytes: 100).Read("Photos/edge.bin", null).Bytes.Length.ShouldBe(100);
    }

    [Fact]
    public void Hidden_paths_symlinks_directories_and_fifos_are_refused()
    {
        _vault.File("Photos/.hidden.jpg", "x");
        Directory.CreateDirectory(Path.Combine(_vault.Path, "Photos/dir"));
        var outside = Path.Combine(Path.GetTempPath(), $"knapper-outside-{Guid.NewGuid():N}");
        File.WriteAllText(outside, "secret");
        File.CreateSymbolicLink(Path.Combine(_vault.Path, "Photos/link.jpg"), outside);
        var fifo = Path.Combine(_vault.Path, "Photos/pipe.jpg");
        using (var p = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { fifo } })!)
            p.WaitForExit();

        var reader = Reader(["Photos"]);
        try
        {
            Should.Throw<KnapperException>(() => reader.Read("Photos/.hidden.jpg", null));
            Should.Throw<KnapperException>(() => reader.Read("Photos/dir", null)).Code.ShouldBe(VaultErrorCode.InvalidArgument);
            Should.Throw<KnapperException>(() => reader.Read("Photos/link.jpg", null)).Code.ShouldBe(VaultErrorCode.SymlinkRejected);
            Should.Throw<KnapperException>(() => reader.Read("Photos/pipe.jpg", null)).Code.ShouldBe(VaultErrorCode.InvalidArgument);
            Should.Throw<KnapperException>(() => reader.Read("Photos/missing.jpg", null)).Code.ShouldBe(VaultErrorCode.NotFound);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    /// <summary>
    /// The torn-read guard (transfer-grants §5.1): a write landing between the
    /// two reads of an attempt makes them disagree, so the reader retries, and
    /// what it serves is a version that existed on disk — never a mix.
    /// </summary>
    [Fact]
    public void An_in_place_write_during_the_read_is_retried_and_never_served_torn()
    {
        var path = _vault.File("Photos/live.txt", "version-one");
        var reader = Reader(["Photos"]);
        reader.BetweenReadsTestHook = attempt =>
        {
            if (attempt == 0)
            {
                // In place, same length, mtime restored: only content differs.
                var mtime = File.GetLastWriteTimeUtc(path);
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
                    fs.Write("version-two"u8);
                File.SetLastWriteTimeUtc(path, mtime);
            }
        };

        var snapshot = reader.Read("Photos/live.txt", null);

        System.Text.Encoding.UTF8.GetString(snapshot.Bytes).ShouldBe("version-two");
        snapshot.Sha256.ShouldBe(VaultHash.Sha256Hex("version-two"u8));
    }

    [Fact]
    public void A_file_that_never_settles_fails_rather_than_serving_a_guess()
    {
        var path = _vault.File("Photos/busy.txt", "0000");
        var reader = Reader(["Photos"]);
        reader.BetweenReadsTestHook = attempt => File.WriteAllText(path, $"{attempt + 1:D4}");

        Should.Throw<KnapperException>(() => reader.Read("Photos/busy.txt", null)).Code.ShouldBe(VaultErrorCode.IoError);
    }

    [Fact]
    public void A_file_that_grows_mid_read_is_retried()
    {
        var path = _vault.File("Photos/grow.txt", "short");
        var reader = Reader(["Photos"]);
        reader.BetweenReadsTestHook = attempt =>
        {
            if (attempt == 0)
                File.AppendAllText(path, " and longer");
        };
        System.Text.Encoding.UTF8.GetString(reader.Read("Photos/grow.txt", null).Bytes).ShouldBe("short and longer");
    }

    /// <summary>
    /// Review 2026-11-08: capacity is reserved from the stat's size BEFORE
    /// the file's bytes are allocated, so a full budget refuses with nothing
    /// read at all.
    /// </summary>
    [Fact]
    public void A_full_budget_refuses_before_any_byte_is_read()
    {
        File.WriteAllBytes(_vault.File("Photos/a.bin"), new byte[50]);
        var reader = Reader(["Photos"]);
        var reads = 0;
        reader.BetweenReadsTestHook = _ => reads++;
        long? asked = null;

        Should.Throw<KnapperException>(() => reader.Read("Photos/a.bin", null, size =>
        {
            asked = size;
            throw new KnapperException(VaultErrorCode.DownloadCapacityExceeded, "full");
        })).Code.ShouldBe(VaultErrorCode.DownloadCapacityExceeded);

        asked.ShouldBe(50);
        reads.ShouldBe(0);
    }

    [Fact]
    public void A_size_change_between_attempts_re_reserves_the_new_size_and_frees_the_old()
    {
        var path = _vault.File("Photos/grow.txt", "short");
        using var store = new DownloadGrantStore(new TransferOptions { MaxBufferedBytes = 1000 }, new DownloadOptions());
        var principal = new TransferPrincipal("i", "sub", "s");
        var reader = Reader(["Photos"]);
        reader.BetweenReadsTestHook = attempt =>
        {
            if (attempt == 0)
                File.AppendAllText(path, " and longer");
        };

        var snapshot = reader.Read("Photos/grow.txt", null, size => store.Reserve(principal, size));

        snapshot.Reservation.ShouldNotBeNull().Bytes.ShouldBe(snapshot.Bytes.LongLength);
        store.BufferedBytes.ShouldBe(snapshot.Bytes.LongLength); // the first reservation was released
        snapshot.Reservation.Dispose();
        store.BufferedBytes.ShouldBe(0);
    }

    [Fact]
    public void A_stale_expectation_releases_its_reservation()
    {
        _vault.File("Photos/a.txt", "now");
        using var store = new DownloadGrantStore(new TransferOptions { MaxBufferedBytes = 1000 }, new DownloadOptions());
        var principal = new TransferPrincipal("i", "sub", "s");
        Should.Throw<KnapperException>(() => Reader(["Photos"]).Read("Photos/a.txt", new string('0', 64),
            size => store.Reserve(principal, size)));
        store.BufferedBytes.ShouldBe(0);
    }
}
