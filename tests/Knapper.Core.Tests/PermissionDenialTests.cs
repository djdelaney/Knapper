namespace Knapper.Core.Tests;

/// <summary>
/// <see cref="PermissionDenialFactAttribute"/> skips its tests on a box that
/// does not refuse access (root). Where they are REQUIRED to run — CI — this
/// is the one test that names the cause when the runner cannot honor that,
/// instead of leaving it to be reverse-engineered from a scatter of failed
/// permission assertions.
/// </summary>
public sealed class PermissionDenialTests
{
    [Fact]
    public void Permission_denial_is_enforced_wherever_it_is_required()
    {
        if (!PermissionDenial.Required)
            return;

        PermissionDenial.IsEnforced.ShouldBeTrue(
            $"{PermissionDenial.RequireVariable}=1 but this process can read a mode-000 file or write a " +
            "read-only directory (running as root?) — every [PermissionDenialFact] test would be measuring " +
            "nothing. Run the suite unprivileged.");
    }
}
