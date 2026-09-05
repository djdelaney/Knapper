using System.Text.Json;

namespace Knapper.Core;

/// <summary>
/// What a tool's published inputSchema/outputSchema must look like for a
/// client to be able to LOAD the manifest at all.
///
/// Lives in Core for the same reason <see cref="ToolNames"/> does: two
/// assemblies check the same property and neither may own it — a repo test
/// asserts it over the in-process server's manifest at build time, and
/// <c>knapper verify --url</c> asserts it against a DEPLOYED server from
/// another assembly. One definition, so the build-time gate and the
/// deployment gate cannot drift into disagreeing about what a loadable
/// manifest is.
///
/// The rule this exists to enforce: under JSON Schema draft 2020-12 a bare
/// <c>true</c> is a legal subschema meaning "anything validates", and the C#
/// generator emits exactly that for a tool method returning a loosely typed
/// <c>object</c>. Nothing in this repo fails when it does — the tool answers
/// calls correctly and every test that does not read the manifest stays
/// green — but strict clients (Claude Code among them) reject the tool list
/// and discard the WHOLE response, so one loosely typed return value takes
/// all thirteen tools offline. Shipped here in 0.3.2; it blocked a cutover.
/// </summary>
public static class ToolSchemaContract
{
    /// <summary>
    /// The most characters of server-authored PROSE a client is known to
    /// deliver. Claude Code cuts every such field at 2048 characters,
    /// silently and with no error on either side — measured 2026-09-05
    /// against delivered copies of both kinds of field, each of which ended
    /// mid-sentence at exactly index 2048. This sits below that, because the
    /// cap is client-defined, undocumented and unversioned: a moving target
    /// to keep clear of, never a size to tune up against.
    ///
    /// The cap is per FIELD, not per manifest — fourteen descriptions plus
    /// the instructions each get their own 2048 — so the fix for prose that
    /// does not fit is never "move it to the other channel". Both channels
    /// have the same ceiling. It is either cut, or moved to a field with
    /// room.
    /// </summary>
    public const int ClientTextBudget = 1950;

    /// <summary>
    /// Whether a piece of server-authored prose survives delivery intact.
    /// This is invisible from the server: the full string is sent, the
    /// manifest is well-formed, the tool answers calls correctly, and every
    /// test that reads the SERVER's copy passes — while the agent acts on
    /// text missing its tail. Shipped here twice at once: the server
    /// instructions lost most of TRUST MODEL (the section whose absence is a
    /// security property), and <c>vault_lint</c> lost the sentence saying its
    /// findings are a standing backlog rather than a list of what changed —
    /// dropped mid-word, so the description ended in a fragment.
    ///
    /// A cut always takes the TAIL, so length is only half the rule: prose
    /// that must survive belongs at the FRONT of its field. That half cannot
    /// be checked mechanically and lives in the comment beside each string.
    /// </summary>
    public static IReadOnlyList<string> FindOverBudgetText(string what, string? text)
    {
        var problems = new List<string>();
        if (text is null || text.Length <= ClientTextBudget)
            return problems;

        problems.Add(
            $"{what}: {text.Length} characters, over the {ClientTextBudget} budget — clients deliver only " +
            $"the first 2048 and say nothing, so this arrives ending \u2026{Tail(text)}");
        return problems;
    }

    private static string Tail(string text) =>
        text.Length <= 2048 ? text[^40..] : text[(2048 - 40)..2048];

    /// <summary>
    /// Returns one human-readable problem per violation — empty means the
    /// manifest entry is loadable. Schemas arrive as raw JSON because that is
    /// what both callers have: the deployed server's wire bytes.
    /// </summary>
    public static IReadOnlyList<string> Validate(string toolName, JsonElement? inputSchema, JsonElement? outputSchema)
    {
        var problems = new List<string>();

        // inputSchema is mandatory per the MCP spec; outputSchema is optional,
        // and ABSENT is valid (the client then treats output as unstructured).
        // Present-but-permissive is the failure — it claims to describe the
        // output and describes nothing.
        if (inputSchema is null)
            problems.Add($"{toolName}: inputSchema is absent (the MCP spec requires it)");
        else
            Inspect(toolName, "inputSchema", inputSchema.Value, problems);

        if (outputSchema is { } output)
            Inspect(toolName, "outputSchema", output, problems);

        return problems;
    }

    /// <summary>
    /// The other half of the same contract: a published schema is a claim
    /// about the RESPONSES, and a response that omits a property its own
    /// schema marks <c>required</c> is rejected by any client that validates
    /// structured content — which the MCP spec says clients SHOULD do. The
    /// SDK's own client does not, so this cannot be left to the wire tests:
    /// they would stay green while real agents saw a broken tool.
    ///
    /// Only omissions are reported. Full JSON Schema validation is not the
    /// job here (no validator ships in the BCL, and type checking is what the
    /// C# types already guarantee); the required/omitted disagreement is the
    /// one that arises from the serializer and the schema exporter holding
    /// opposite defaults about nulls.
    /// </summary>
    public static IReadOnlyList<string> FindMissingRequired(
        string toolName, JsonElement? outputSchema, JsonElement? structuredContent)
    {
        var problems = new List<string>();
        if (outputSchema is not { } schema)
            return problems;
        if (structuredContent is not { } content)
        {
            problems.Add($"{toolName}: publishes an outputSchema but returned no structured content");
            return problems;
        }
        CheckRequired(toolName, "result", schema, content, problems);
        return problems;
    }

    private static void CheckRequired(
        string toolName, string path, JsonElement schema, JsonElement value, List<string> problems)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return;

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
            {
                foreach (var name in required.EnumerateArray())
                {
                    if (name.GetString() is { } field && !value.TryGetProperty(field, out _))
                    {
                        problems.Add(
                            $"{toolName}: {path}.{field} is required by the published schema but absent from the " +
                            "response (a null dropped by the serializer, or a schema that outran the payload)");
                    }
                }
            }
            if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            {
                foreach (var member in value.EnumerateObject())
                {
                    if (properties.TryGetProperty(member.Name, out var memberSchema))
                        CheckRequired(toolName, $"{path}.{member.Name}", memberSchema, member.Value, problems);
                }
            }
            return;
        }

        // An empty array validates trivially — and an all-empty response is
        // the normal state of a fresh vault, so this is where a conformance
        // check quietly stops checking. The caller's job is to feed it data.
        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var itemSchema))
        {
            var index = 0;
            foreach (var element in value.EnumerateArray())
                CheckRequired(toolName, $"{path}[{index++}]", itemSchema, element, problems);
        }
    }

    private static void Inspect(string toolName, string which, JsonElement schema, List<string> problems)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            problems.Add(
                $"{toolName}: {which} is {Describe(schema)}, not a schema object — a permissive schema is " +
                "legal draft 2020-12 and useless to a client; give the tool method a concrete return type");
            return;
        }
        if (!schema.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
            || type.GetString() != "object")
        {
            problems.Add(
                $"{toolName}: {which} has no \"type\": \"object\" at its root — MCP tool schemas describe an " +
                "argument/result OBJECT");
        }
        WalkSubschemas(toolName, which, which, schema, problems);
    }

    /// <summary>
    /// Every position where a SUBSCHEMA appears must hold an object too: a
    /// nested loosely typed member emits the same useless <c>true</c> one
    /// level down, where the top-level check cannot see it. Boolean-valued
    /// keywords that are NOT subschema positions (<c>additionalProperties:
    /// false</c> above all) are deliberately not walked — they are ordinary,
    /// correct, and flagging them would make this gate cry wolf.
    /// </summary>
    private static void WalkSubschemas(
        string toolName, string which, string path, JsonElement schema, List<string> problems)
    {
        foreach (var keyword in new[] { "properties", "$defs", "definitions", "patternProperties" })
        {
            if (!schema.TryGetProperty(keyword, out var map) || map.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var member in map.EnumerateObject())
                Descend(toolName, which, $"{path}.{keyword}.{member.Name}", member.Value, problems);
        }

        if (schema.TryGetProperty("items", out var items))
            Descend(toolName, which, $"{path}.items", items, problems);

        foreach (var keyword in new[] { "anyOf", "oneOf", "allOf", "prefixItems" })
        {
            if (!schema.TryGetProperty(keyword, out var list) || list.ValueKind != JsonValueKind.Array)
                continue;
            var index = 0;
            foreach (var branch in list.EnumerateArray())
                Descend(toolName, which, $"{path}.{keyword}[{index++}]", branch, problems);
        }
    }

    private static void Descend(
        string toolName, string which, string path, JsonElement subschema, List<string> problems)
    {
        if (subschema.ValueKind != JsonValueKind.Object)
        {
            problems.Add(
                $"{toolName}: {path} is {Describe(subschema)}, not a schema object — strict clients reject the " +
                "whole tool list over it (the usual cause is a loosely typed 'object' member)");
            return;
        }
        WalkSubschemas(toolName, which, path, subschema, problems);
    }

    private static string Describe(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True or JsonValueKind.False => $"the boolean {element.GetRawText()}",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.Null => "null",
        _ => element.ValueKind.ToString(),
    };
}
