namespace Knapper.Core.Vault;

/// <summary>
/// Detects a case-insensitive filesystem (the macOS dev default). Per-path
/// lock identity is SHA-256 of the relative path STRING, batch duplicate
/// rejection and move same-path checks are ordinal compares, and search
/// prefixes are distinct strings — on a case-insensitive FS two spellings
/// alias one inode and "mutations to one file are serialized" silently
/// voids. Case-folding is NOT the fix: production ext4 legitimately hosts
/// names differing only by case, and folding would falsely reject valid
/// batches there. So a case-SENSITIVE vault filesystem is a hard production
/// requirement; this probe is how `knapper doctor` (gate) and server
/// startup (warning — dev boxes are legitimately case-insensitive) notice.
/// </summary>
public static class CaseSensitivityProbe
{
    /// <summary>
    /// True when <paramref name="directory"/>'s filesystem resolves names
    /// case-insensitively. Probes with a Knapper temp-prefixed file
    /// (Sync-ignored, gitignored, unaddressable via the resolver), removed
    /// before returning.
    /// </summary>
    public static bool IsCaseInsensitive(string directory)
    {
        var name = AtomicFile.TempPrefix + "case-" + Guid.NewGuid().ToString("N")[..12] + "-a";
        var probe = Path.Combine(directory, name);
        try
        {
            File.WriteAllBytes(probe, []);
            return File.Exists(Path.Combine(directory, name[..^1] + "A"));
        }
        finally
        {
            try
            {
                File.Delete(probe);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
