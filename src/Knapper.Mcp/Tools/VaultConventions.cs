using System.Runtime.CompilerServices;
using Knapper.Core;
using Knapper.Core.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Knapper.Mcp.Tools;

/// <summary>
/// The vault's note-writing conventions as the write tools' descriptions
/// state them — composed at startup from <c>Conventions:*</c>, not compiled
/// in.
///
/// WHY DESCRIPTIONS. A tool description is the only channel that reaches an
/// agent at the moment it is drafting. Server instructions arrive once at
/// initialize, thousands of tokens before the write, and the vault's own
/// CLAUDE.md arrives only if the agent thought to read it — which is the
/// problem this exists to close.
///
/// WHY CONFIGURATION. These were compile-time constants until 0.10.0, which
/// shipped ONE vault's conventions (its default folder included) to every
/// deployment of a public build. Each deployment now states its own, and one
/// that states nothing publishes no convention text at all. Each rule flag
/// drives both the sentence here and the matching write warning
/// (<see cref="Knapper.Core.Mutation.ConventionChecker"/>), so what the
/// description claims and what the server checks cannot drift apart.
///
/// WHY STARTUP REFUSES AN OVERLONG RESULT. Clients deliver the first 2048
/// characters of a description and say nothing. A composed description is no
/// longer a constant a test can read, so the budget check that
/// <c>ToolManifestTests</c> runs at build time is repeated here against the
/// text actually being served.
///
/// The archived-subtree clauses stay constants: they describe server
/// behaviour, which is the same for every deployment.
/// </summary>
internal static class VaultConventions
{
    /// <summary>
    /// What a query says about an archived subtree. Goes on the DISCOVERY
    /// surfaces, because the failure it prevents is an agent reading an
    /// exhaustive-looking empty result as "no such note" when the note is
    /// simply outside the default scope. The envelope's excludedPrefixes
    /// carries the same fact per response; this is what makes an agent expect
    /// it and know what to do about it.
    /// </summary>
    public const string ArchivedScope =
        " Archived subtrees are skipped by default; excludedPrefixes names them — scope to one to reach it.";

    /// <summary>
    /// The write-side half. An error an agent cannot act on becomes a retry
    /// loop or a workaround, so the clause says what IS allowed rather than
    /// only what is not.
    /// </summary>
    public const string ArchivedWrites =
        " [PathArchived]: the path is in an archived subtree. Creating and moving INTO one is allowed; " +
        "changing what is already there is a human action.";

    /// <summary>
    /// Which clauses each tool carries. The writing clause goes on every tool
    /// that writes note CONTENT; the placement clause on every tool that
    /// chooses a PATH. vault_delete carries neither: it removes a note, so
    /// neither how one is written nor where one goes is in play.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, (bool Writing, bool Placement)> Clauses =
        new Dictionary<string, (bool, bool)>(StringComparer.Ordinal)
        {
            ["vault_edit"] = (true, false),
            ["vault_append"] = (true, false),
            ["vault_batch"] = (true, false),
            ["vault_create"] = (true, true),
            ["vault_mkdir"] = (false, true),
            ["vault_move"] = (false, true),
        };

    /// <summary>How the content itself is written; null when no writing convention is configured.</summary>
    internal static string? Writing(ConventionsOptions o)
    {
        var rules = new List<string>();
        if (o.WikilinksOnly)
            rules.Add("internal links are [[wikilinks]], never markdown links");
        if (o.NoNewFrontmatter && o.NoNewTags)
            rules.Add("do NOT add frontmatter to a note that lacks it, or tags to a note that does not use them");
        else if (o.NoNewFrontmatter)
            rules.Add("do NOT add frontmatter to a note that lacks it");
        else if (o.NoNewTags)
            rules.Add("do NOT add tags to a note that does not use them");
        if (!string.IsNullOrWhiteSpace(o.Style))
            rules.Add(o.Style.Trim().TrimEnd('.'));
        if (rules.Count == 0)
            return null;

        var clause = " CONVENTIONS: " + string.Join("; ", rules) + ".";
        if (o.AnyChecked)
            clause += " The response's warnings name any checked convention this write broke — fix it with a follow-up edit.";
        return clause;
    }

    /// <summary>Where content goes; null when no default folder is configured.</summary>
    internal static string? Placement(ConventionsOptions o)
    {
        var folder = o.NewNoteFolder?.Trim().Trim('/');
        return string.IsNullOrEmpty(folder)
            ? null
            : $" New notes default to {folder}/ unless a more specific folder clearly fits; the folder hierarchy " +
              "is not yours to reorganize. A note placed at the vault root comes back in the response's warnings.";
    }

    /// <summary>
    /// Each tool's description as its attribute wrote it, captured the first
    /// time it is seen. Composition always starts from here, so running
    /// <see cref="Apply"/> twice over the same tool objects (an options
    /// rebuild) cannot append the clauses twice.
    /// </summary>
    private static readonly ConditionalWeakTable<Tool, string> BaseDescriptions = [];

    /// <summary>
    /// Append each tool's configured clauses to its published description.
    /// Returns every description the result pushes over the client's
    /// delivery budget; the caller refuses to start on any.
    /// </summary>
    internal static IReadOnlyList<string> Apply(IEnumerable<McpServerTool> tools, ConventionsOptions o)
    {
        var writing = Writing(o);
        var placement = Placement(o);
        var problems = new List<string>();
        foreach (var tool in tools)
        {
            var protocol = tool.ProtocolTool;
            if (!Clauses.TryGetValue(protocol.Name, out var carries))
                continue;
            var description = BaseDescriptions.GetValue(protocol, t => t.Description ?? "");
            if (carries.Placement && placement is not null)
                description += placement;
            if (carries.Writing && writing is not null)
                description += writing;
            protocol.Description = description;
            problems.AddRange(ToolSchemaContract.FindOverBudgetText($"{protocol.Name} description", description));
        }
        return problems;
    }
}
