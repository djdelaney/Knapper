namespace Knapper.Core;

/// <summary>
/// The locked tool NAMES as clients see them. Lives in Core because two
/// assemblies need them and neither may own them: <c>Knapper.Mcp</c>'s
/// ToolSurface maps them to implementing types, and <c>knapper verify --url</c>
/// asserts a deployed server exposes exactly this set — which is the whole
/// value of that check, since a partially-registered surface (see
/// ToolSurface.Resolve's overload note) answers tools/list without complaint.
///
/// A test pins this list against ToolSurface.All, which a second test pins
/// against the <c>[McpServerTool(Name = …)]</c> attributes. Three places, one
/// contract, no drift: a rename is a version bump, not a refactor.
/// There is no unconditional-write tool here and never will be (brief §15).
/// </summary>
public static class ToolNames
{
    public static readonly IReadOnlyList<string> All =
    [
        "vault_files",
        "vault_search",
        "vault_search_frontmatter",
        "vault_read",
        "vault_batch_read",
        "vault_stat",
        "vault_edit",
        "vault_append",
        "vault_create",
        "vault_mkdir",
        "vault_move",
        "vault_delete",
        "vault_batch",
    ];
}
