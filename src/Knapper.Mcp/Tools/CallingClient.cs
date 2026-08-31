namespace Knapper.Mcp.Tools;

/// <summary>
/// The calling client application's name, carried from the tools/call filter
/// (Program.cs) to <see cref="ToolSupport"/>'s log line — the one datum the
/// Cloudflare Access identity structurally cannot supply, because that
/// identity is per-USER while the round-trip cost <c>ops/call-economics.sh</c>
/// measures is a property of the SURFACE.
///
/// <see cref="AsyncLocal{T}"/> rather than a field: <see cref="ToolSupport"/>
/// is a singleton shared by every concurrent session, so a field would
/// attribute whichever call logged last to whichever call is logging now —
/// a wrong attribution that looks exactly like a right one. The filter wraps
/// the tool invocation, so the value flows into the body it belongs to and
/// nowhere else.
/// </summary>
internal static class CallingClient
{
    private static readonly AsyncLocal<string?> Current = new();

    internal static string? Name
    {
        get => Current.Value;
        set => Current.Value = value;
    }
}
