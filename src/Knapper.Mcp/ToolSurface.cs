using Knapper.Mcp.Tools;

namespace Knapper.Mcp;

/// <summary>
/// The locked tool-name contract → implementing classes, and the resolver for
/// <c>Mcp:DisabledTools</c>. A disabled tool is absent from tools/list AND
/// tools/call. An unknown name fails startup: this option exists for security
/// posture, and a typo would silently leave the tool it meant to disable
/// exposed. There is no unconditional-write tool in this table and never will
/// be (brief §15).
/// </summary>
internal static class ToolSurface
{
    // Keep in lockstep with the [McpServerTool(Name = ...)] attributes; a test
    // reflects the attributes and fails on drift.
    internal static readonly IReadOnlyDictionary<string, Type> All =
        new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
        {
            ["vault_files"] = typeof(VaultFilesTool),
            ["vault_search"] = typeof(VaultSearchTool),
            ["vault_search_frontmatter"] = typeof(VaultFrontmatterTool),
            ["vault_lint"] = typeof(VaultLintTool),
            ["vault_read"] = typeof(VaultReadTool),
            ["vault_batch_read"] = typeof(VaultBatchReadTool),
            ["vault_stat"] = typeof(VaultStatTool),
            ["vault_edit"] = typeof(VaultEditTool),
            ["vault_append"] = typeof(VaultAppendTool),
            ["vault_create"] = typeof(VaultCreateTool),
            ["vault_mkdir"] = typeof(VaultMkdirTool),
            ["vault_move"] = typeof(VaultMoveTool),
            ["vault_delete"] = typeof(VaultDeleteTool),
            ["vault_batch"] = typeof(VaultBatchTool),
        };

    // Declared IEnumerable<Type>, NOT IReadOnlyList<Type>, and load-bearing:
    // the SDK has both WithTools(IEnumerable<Type>) and a generic
    // WithTools<TToolType>(TToolType singleToolInstance). For an argument
    // statically typed IReadOnlyList<Type>, C# prefers the GENERIC overload
    // (identity beats implicit reference conversion) — which registers the
    // LIST ITSELF as one tool object with zero [McpServerTool] methods, and
    // the server silently exposes no tools at all.
    internal static IEnumerable<Type> Resolve(IEnumerable<string>? disabledTools)
    {
        var disabled = new HashSet<string>(
            (disabledTools ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var unknown = disabled.Where(n => !All.ContainsKey(n)).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"Mcp:DisabledTools contains unknown tool name(s): {string.Join(", ", unknown)}. " +
                $"Valid names: {string.Join(", ", All.Keys.OrderBy(k => k, StringComparer.Ordinal))}. " +
                "Refusing to start — a typo here would silently leave the tool it meant to disable exposed.");
        }
        return All.Where(kv => !disabled.Contains(kv.Key)).Select(kv => kv.Value).ToList();
    }
}
