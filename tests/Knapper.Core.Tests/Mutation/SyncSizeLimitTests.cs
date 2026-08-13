using System.Text;
using Knapper.Core.Mutation;

namespace Knapper.Core.Tests.Mutation;

/// <summary>
/// A write larger than Obsidian Sync will carry is refused, on every write
/// path, before anything lands.
///
/// The failure this prevents (found at deployment, CT 106 2026-08-13, against
/// v0.1.1): Sync silently refuses any file over its per-file ceiling, logging
/// "File too large to sync (… max 5.00 MB)" and then "Fully synced" in the
/// SAME millisecond. So an oversized note verifies on disk, commits to git,
/// returns a success receipt to the agent, leaves every health signal green —
/// and never reaches a single device. Local content verification is
/// structurally blind to it, because nothing local is wrong.
///
/// The ceiling is measured POST-TRANSFORM throughout. The realistic case is
/// not a huge write; it is a small anchored insert into a note already near
/// the limit, where the input is a few KB.
/// </summary>
public sealed class SyncSizeLimitTests
{
    /// <summary>Every mutation entry point, so a new one cannot quietly skip the guard.</summary>
    [Fact]
    public void Create_refuses_a_file_larger_than_sync_will_carry()
    {
        using var v = new MutationVault();
        var service = v.ServiceWithMaxFileBytes(1000);

        var e = Should.Throw<KnapperException>(() => service.Create("big.md", new string('x', 1001)));

        e.Code.ShouldBe(VaultErrorCode.TooLargeToSync);
        File.Exists(Path.Combine(v.VaultDir.Path, "big.md")).ShouldBeFalse("the write must not land");
    }

    [Fact]
    public void Append_refuses_when_the_RESULT_crosses_the_limit()
    {
        using var v = new MutationVault();
        var service = v.ServiceWithMaxFileBytes(1000);
        var sha = v.Write("near.md", new string('x', 990));

        // The appended text is tiny. The RESULT is what exceeds the ceiling —
        // an input-size check would let this through, which is the whole point.
        var e = Should.Throw<KnapperException>(() => service.Append("near.md", sha, new string('y', 50)));

        e.Code.ShouldBe(VaultErrorCode.TooLargeToSync);
        File.ReadAllText(Path.Combine(v.VaultDir.Path, "near.md")).ShouldBe(new string('x', 990));
    }

    [Fact]
    public void Edit_refuses_when_the_RESULT_crosses_the_limit()
    {
        using var v = new MutationVault();
        var service = v.ServiceWithMaxFileBytes(1000);
        var sha = v.Write("near.md", "HEAD" + new string('x', 986));

        var e = Should.Throw<KnapperException>(() => service.Edit(
            "near.md", sha, [new EditSpec("HEAD", "HEAD" + new string('z', 100), 1)]));

        e.Code.ShouldBe(VaultErrorCode.TooLargeToSync);
        File.ReadAllText(Path.Combine(v.VaultDir.Path, "near.md")).ShouldStartWith("HEAD" + new string('x', 10));
    }

    /// <summary>
    /// In the VALIDATE phase: a bad item fails the whole batch untouched.
    /// Checking during apply would land the earlier items first, which is
    /// exactly the partial application batch validation exists to prevent.
    /// </summary>
    [Fact]
    public void Batch_rejects_the_whole_batch_untouched_when_one_item_is_oversized()
    {
        using var v = new MutationVault();
        var service = v.ServiceWithMaxFileBytes(1000);

        var e = Should.Throw<KnapperException>(() => service.Batch([
            new BatchItem(BatchItemKind.Create, "fine.md") { Text = "small" },
            new BatchItem(BatchItemKind.Create, "big.md") { Text = new string('x', 1001) },
        ]));

        e.Code.ShouldBe(VaultErrorCode.TooLargeToSync);
        File.Exists(Path.Combine(v.VaultDir.Path, "fine.md"))
            .ShouldBeFalse("the VALID item must not land either — batch validates everything first");
        File.Exists(Path.Combine(v.VaultDir.Path, "big.md")).ShouldBeFalse();
    }

    /// <summary>
    /// Bytes, not characters. A note of non-ASCII text has more UTF-8 bytes
    /// than characters, and Sync counts bytes — a character-length check would
    /// pass a file Sync then refuses, which is the original bug wearing a
    /// different hat.
    /// </summary>
    [Fact]
    public void The_limit_counts_utf8_bytes_not_characters()
    {
        using var v = new MutationVault();
        var service = v.ServiceWithMaxFileBytes(1000);

        // 400 characters, 1200 UTF-8 bytes.
        var text = new string('あ', 400);
        Encoding.UTF8.GetByteCount(text).ShouldBe(1200);
        text.Length.ShouldBeLessThan(1000);

        Should.Throw<KnapperException>(() => service.Create("cjk.md", text))
            .Code.ShouldBe(VaultErrorCode.TooLargeToSync);
    }

    [Fact]
    public void A_write_at_exactly_the_limit_is_allowed()
    {
        using var v = new MutationVault();
        var service = v.ServiceWithMaxFileBytes(1000);

        service.Create("exact.md", new string('x', 1000));

        new FileInfo(Path.Combine(v.VaultDir.Path, "exact.md")).Length.ShouldBe(1000);
    }

    /// <summary>
    /// The guard is about NEW bytes. A file that is already over the ceiling —
    /// written by a shell on the box, or predating the guard — must stay movable and
    /// deletable, or the only tools that could tidy it up are the ones refusing
    /// to run. /health's oversized list is how those surface.
    /// </summary>
    [Fact]
    public void An_already_oversized_file_can_still_be_moved_and_deleted()
    {
        using var v = new MutationVault();
        var service = v.ServiceWithMaxFileBytes(1000);
        var sha = v.Write("huge.md", new string('x', 5000));

        service.Move("huge.md", "moved.md", sha);
        File.Exists(Path.Combine(v.VaultDir.Path, "moved.md")).ShouldBeTrue();

        service.Delete("moved.md", sha);
        File.Exists(Path.Combine(v.VaultDir.Path, "moved.md")).ShouldBeFalse();
    }

    /// <summary>
    /// The default is the conservative reading of ob's ambiguous "max 5.00 MB"
    /// (5,000,000, not 5,242,880 — unbisected as of 2026-08-13). Too low
    /// refuses writes loudly; too high strands them silently. Pinned so a
    /// later "tidy up to 5 * 1024 * 1024" has to argue with a test.
    /// </summary>
    [Fact]
    public void The_default_ceiling_is_the_conservative_reading()
    {
        new Knapper.Core.Options.SyncOptions().MaxFileBytes.ShouldBe(5_000_000);
    }
}
