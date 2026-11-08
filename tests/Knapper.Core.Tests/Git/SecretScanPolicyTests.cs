using System.Security.Cryptography;
using System.Text;
using Knapper.Core.Git;
using Knapper.Core.Tests.Mutation;

namespace Knapper.Core.Tests.Git;

/// <summary>
/// The one definition of what the secret scan reads (transfer-grants §4.3).
/// The differential below is the test that matters: it runs the upload's
/// pre-landing verdict and the REAL committer over the same payloads and
/// demands they agree on every one — a pre-scan narrower than the committer
/// admits exactly the files that then refuse every vault commit.
/// </summary>
public sealed class SecretScanPolicyTests
{
    private const string Key = "AKIAIOSFODNN7EXAMPLE";

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Coverage_is_the_size_cap_or_a_text_extension()
    {
        SecretScanPolicy.ShouldScan("Files/a.bin", SecretScanPolicy.MaxScanBytes).ShouldBeTrue();
        SecretScanPolicy.ShouldScan("Files/a.bin", SecretScanPolicy.MaxScanBytes + 1).ShouldBeFalse();
        SecretScanPolicy.ShouldScan("Notes/a.md", SecretScanPolicy.MaxScanBytes + 1).ShouldBeTrue();
        SecretScanPolicy.ShouldScan("Logs/A.LOG", SecretScanPolicy.MaxScanBytes + 1).ShouldBeTrue();
        SecretScanPolicy.ShouldScan("Photos/big.jpg", 5_000_000).ShouldBeFalse();
    }

    [Fact]
    public void Decoding_is_lossy_utf8_that_honours_byte_order_marks()
    {
        SecretScanPolicy.Decode([0xEF, 0xBB, 0xBF, .. Utf8("hi")]).ShouldBe("hi");
        // A UTF-16 mark: the UTF-16 reading leads, the raw lossy-UTF-8 reading follows.
        SecretScanPolicy.Decode([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("wide")])
            .ShouldStartWith("wide\n");
        SecretScanPolicy.Decode([0x63, 0x61, 0x66, 0xE9]).ShouldBe("caf�");
    }

    /// <summary>
    /// Two bytes are all a byte-order mark is. A file that merely starts with
    /// FF FE or FE FF was read wholesale as UTF-16 and its credentials went
    /// unseen by the committer too — found when the differential below was
    /// made to pin known answers instead of mere agreement.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFE })]
    [InlineData(new byte[] { 0xFE, 0xFF })]
    public void A_stray_utf16_mark_cannot_hide_ascii_credentials(byte[] mark)
    {
        SecretScanPolicy.Scan("Files/x.bin", [.. mark, .. Utf8($"\nkey {Key}\n")]).ShouldNotBeEmpty();
        SecretScanPolicy.Scan("Notes/x.md", [.. mark, .. Utf8($"\nkey {Key}\n")]).ShouldNotBeEmpty();
    }

    [Fact]
    public void Text_that_is_not_strict_utf8_and_binaries_under_the_cap_are_scanned()
    {
        byte[] latin1Env = [.. Encoding.Latin1.GetBytes("café\n"), .. Utf8($"AWS={Key}\n")];
        SecretScanPolicy.Scan("Config/app.env", latin1Env).ShouldNotBeEmpty();

        byte[] binary = [.. RandomNumberGenerator.GetBytes(512), .. Utf8($"\n{Key}\n"), .. RandomNumberGenerator.GetBytes(512)];
        SecretScanPolicy.Scan("Files/archive.zip", binary).ShouldNotBeEmpty();
    }

    [Fact]
    public void Findings_are_masked_never_the_secret()
    {
        var finding = SecretScanPolicy.Scan("Notes/k.md", Utf8($"key {Key}\n")).ShouldHaveSingleItem();
        finding.Masked.ShouldNotContain(Key);
    }

    public static TheoryData<string, string, byte[], bool> Payloads()
    {
        var data = new TheoryData<string, string, byte[], bool>();
        void Add(string name, string path, byte[] bytes) =>
            data.Add(name, path, bytes, !name.Contains("clean", StringComparison.Ordinal) && !name.StartsWith("non-text over", StringComparison.Ordinal));
        var overCap = (int)SecretScanPolicy.MaxScanBytes + 1024;

        Add("clean note", "Notes/clean.md", Utf8("nothing to see\n"));
        Add("note with key", "Notes/key.md", Utf8($"aws {Key}\n"));
        Add("UTF-8 BOM note with key", "Notes/bom.md", [0xEF, 0xBB, 0xBF, .. Utf8($"aws {Key}\n")]);
        Add("UTF-16 text with key", "Notes/wide.txt",
            [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes($"aws {Key}\n")]);
        Add("invalid UTF-8 note with key", "Notes/bad.md", [0xC3, 0x28, 0x80, .. Utf8($"\naws {Key}\n")]);
        Add("stray UTF-16 mark before a key", "Notes/marked.md", [0xFF, 0xFE, 0x80, .. Utf8($"\naws {Key}\n")]);
        Add("Latin-1 env with key", "Config/latin.env", [.. Encoding.Latin1.GetBytes("café\n"), .. Utf8($"AWS={Key}\n")]);
        Add("binary under the cap with key", "Files/blob.bin",
            [.. RandomNumberGenerator.GetBytes(4096), .. Utf8($"\n{Key}\n"), .. RandomNumberGenerator.GetBytes(4096)]);
        Add("binary under the cap, clean", "Files/clean.bin", new byte[4096]);
        Add("non-text over the cap with key", "Files/big.bin",
            [.. new byte[overCap], .. Utf8($"\n{Key}\n")]);
        Add("log over the cap with key", "Logs/big.log",
            [.. Utf8(new string('x', overCap)), .. Utf8($"\n{Key}\n")]);
        return data;
    }

    /// <summary>
    /// One payload per fresh vault, so each verdict is about that payload
    /// alone: the committer either commits it or refuses with the
    /// credential-shaped finding.
    /// </summary>
    [Theory]
    [MemberData(nameof(Payloads))]
    public void The_pre_landing_verdict_and_the_committers_verdict_always_agree(
        string name, string path, byte[] bytes, bool expectRefused)
    {
        using var vault = new MutationVault();
        var job = new GitCommitJob(vault.Resolver, vault.Locks);
        job.Init();
        File.WriteAllBytes(vault.VaultDir.File(path), bytes);

        var preLanding = SecretScanPolicy.Scan(path, bytes).Count > 0;
        bool committerRefused;
        try
        {
            job.Commit(TimeSpan.FromSeconds(30)).Committed.ShouldBeTrue(name);
            committerRefused = false;
        }
        catch (KnapperException e) when (e.Code == VaultErrorCode.MutationBlocked
            && e.Message.Contains("credential-shaped", StringComparison.Ordinal))
        {
            committerRefused = true;
        }

        // Agreement alone could be vacuous (both blind); each case also has
        // a known answer.
        committerRefused.ShouldBe(expectRefused, $"{name}: the committer's verdict");
        preLanding.ShouldBe(committerRefused, $"{name}: pre-landing {(preLanding ? "refuses" : "admits")} it, " +
            $"the committer {(committerRefused ? "refuses" : "commits")} it");
    }

    // ---- git attributes that re-encode on `add` (review 2026-11-08) ----

    private static (MutationVault Vault, GitCommitJob Job) Repo()
    {
        var vault = new MutationVault();
        var job = new GitCommitJob(vault.Resolver, vault.Locks);
        job.Init();
        return (vault, job);
    }

    /// <summary>
    /// The reviewer's reproduction: a UTF-16LE credential with no mark is
    /// invisible to the pre-scan, and `working-tree-encoding` converts it to
    /// UTF-8 on `add`, where the committer would find it — so an upload would
    /// land and then refuse every commit. The committer now refuses the
    /// ATTRIBUTE, before any scan verdict could diverge, and leaves the index
    /// as Sync left it.
    /// </summary>
    [Fact]
    public void A_re_encoding_attribute_refuses_the_commit_before_the_views_can_diverge()
    {
        var (vault, job) = Repo();
        using (vault)
        {
            File.WriteAllText(Path.Combine(vault.VaultDir.Path, ".gitattributes"), "*.ps1 working-tree-encoding=UTF-16LE\n");
            var utf16NoMark = Encoding.Unicode.GetBytes($"$k = '{Key}'\n");
            File.WriteAllBytes(vault.VaultDir.File("Scripts/x.ps1"), utf16NoMark);

            SecretScanPolicy.Scan("Scripts/x.ps1", utf16NoMark).ShouldBeEmpty(); // the pre-scan's view

            var refused = Should.Throw<KnapperException>(() => job.Commit(TimeSpan.FromSeconds(30)));
            refused.Code.ShouldBe(VaultErrorCode.MutationBlocked);
            refused.Message.ShouldContain("re-encode");
            refused.Message.ShouldNotContain(Key);
            Staged(vault).ShouldBeEmpty("the index is reset on refusal");
        }
    }

    [Fact]
    public void The_attribute_is_refused_even_over_clean_content_and_from_info_attributes()
    {
        var (vault, job) = Repo();
        using (vault)
        {
            Directory.CreateDirectory(Path.Combine(vault.VaultDir.Path, ".git", "info"));
            File.WriteAllText(Path.Combine(vault.VaultDir.Path, ".git", "info", "attributes"),
                "*.txt working-tree-encoding=UTF-16\n");
            File.WriteAllBytes(vault.VaultDir.File("Notes/clean.txt"), Encoding.BigEndianUnicode.GetBytes("\uFEFFfine\n"));

            Should.Throw<KnapperException>(() => job.Commit(TimeSpan.FromSeconds(30)))
                .Message.ShouldContain("re-encode");
        }
    }

    [Fact]
    public void Attributes_that_cannot_create_a_match_still_commit()
    {
        var (vault, job) = Repo();
        using (vault)
        {
            File.WriteAllText(Path.Combine(vault.VaultDir.Path, ".gitattributes"), "*.md text eol=lf\n*.sh ident\n");
            File.WriteAllText(vault.VaultDir.File("Notes/a.md"), "line\r\n");
            File.WriteAllText(vault.VaultDir.File("Scripts/b.sh"), "# $Id$\n");

            job.Commit(TimeSpan.FromSeconds(30)).Committed.ShouldBeTrue();
        }
    }

    private static string Staged(MutationVault vault)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git") { RedirectStandardOutput = true };
        foreach (var a in new[] { "-C", vault.VaultDir.Path, "diff", "--cached", "--name-only" })
            psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output.Trim();
    }
}
