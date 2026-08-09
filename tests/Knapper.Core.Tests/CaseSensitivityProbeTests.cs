using Knapper.Core.Vault;

namespace Knapper.Core.Tests;

public sealed class CaseSensitivityProbeTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Probe_agrees_with_the_filesystems_actual_behavior()
    {
        // Environment-independent: compare the probe's answer to a direct
        // observation of the same filesystem (runs true on default APFS,
        // false on ext4 — both must agree with reality).
        var witness = Path.Combine(_dir.Path, "case-witness-a");
        File.WriteAllBytes(witness, []);
        var observed = File.Exists(Path.Combine(_dir.Path, "case-witness-A"));

        CaseSensitivityProbe.IsCaseInsensitive(_dir.Path).ShouldBe(observed);
    }

    [Fact]
    public void Probe_cleans_up_after_itself()
    {
        CaseSensitivityProbe.IsCaseInsensitive(_dir.Path);
        Directory.EnumerateFileSystemEntries(_dir.Path)
            .Where(p => Path.GetFileName(p).StartsWith(AtomicFile.TempPrefix, StringComparison.Ordinal))
            .ShouldBeEmpty();
    }
}
