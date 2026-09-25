namespace Knapper.TestSupport;

/// <summary>
/// A <c>[Fact]</c> whose assertion needs the OS to REFUSE access: it takes a
/// permission away (mode 000 file, read-only directory, read-only audit sink)
/// and pins what Knapper does when the read or write fails. Root ignores
/// file modes, so under root the refusal never arrives and the test fails for
/// a reason that has nothing to do with Knapper — which is every Claude cloud
/// session (uid 0), where eleven of these used to bury any real failure in
/// the same run.
///
/// It skips — visibly, with <see cref="PermissionDenial.SkipReason"/> — only
/// when <see cref="PermissionDenial.IsEnforced"/> MEASURES that refusal
/// does not happen, never on a uid check: what the tests depend on is the
/// refusal, and a capability can grant or withhold it independently of the
/// user name. CI sets <c>KNAPPER_REQUIRE_PERMISSION_DENIAL=1</c>, which turns
/// the skip off: these tests are only worth anything where they run, so the
/// one runner that is supposed to run them can never quietly stop.
///
/// Linked into every test project from <c>tests/Shared/</c> — ONE probe, so
/// two projects cannot disagree about whether this box enforces permissions.
/// </summary>
public sealed class PermissionDenialFactAttribute : FactAttribute
{
    public PermissionDenialFactAttribute()
    {
        if (!PermissionDenial.Required && !PermissionDenial.IsEnforced)
            Skip = PermissionDenial.SkipReason;
    }
}

public static class PermissionDenial
{
    public const string RequireVariable = "KNAPPER_REQUIRE_PERMISSION_DENIAL";

    public const string SkipReason =
        "needs the OS to refuse access, and this process bypasses file permissions (running as root?). " +
        "CI runs it unprivileged with " + RequireVariable + "=1.";

    /// <summary>Set by CI: never skip, whatever the probe says.</summary>
    public static bool Required =>
        Environment.GetEnvironmentVariable(RequireVariable) == "1";

    private static readonly Lazy<bool> Enforced = new(Probe);

    /// <summary>
    /// True when a mode-000 file cannot be read AND a read-only directory
    /// cannot be written — the two refusals the tagged tests stand on.
    /// </summary>
    public static bool IsEnforced => Enforced.Value;

    private static bool Probe()
    {
        string? dir = null;
        try
        {
            dir = Directory.CreateTempSubdirectory("knapper-permission-probe-").FullName;
            var file = Path.Combine(dir, "unreadable");
            File.WriteAllText(file, "x");
            File.SetUnixFileMode(file, UnixFileMode.None);
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            return Refused(() => File.ReadAllBytes(file))
                && Refused(() => File.WriteAllText(Path.Combine(dir, "new"), "x"));
        }
        catch (Exception)
        {
            // A probe that could not run answers "enforced": the tests then
            // run and report what really happens. Unknown must never become
            // a skip.
            return true;
        }
        finally
        {
            if (dir is not null)
            {
                try
                {
                    File.SetUnixFileMode(dir,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    Directory.Delete(dir, recursive: true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static bool Refused(Action attempt)
    {
        try
        {
            attempt();
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
