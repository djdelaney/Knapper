using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;

namespace Knapper.Mcp;

/// <summary>
/// How tool results are serialized — and it is a CONTRACT question, not a
/// formatting preference.
///
/// The SDK's default options ignore nulls when writing, while the schema
/// exporter marks every property without a default value <c>required</c>.
/// Those two defaults disagree: a null <c>nextCursor</c> vanishes from the
/// payload while the published outputSchema still requires it, so every
/// untruncated response VIOLATES the schema the same response advertises.
/// Nothing here notices — the SDK's own client does not validate results —
/// but a client that does (the MCP spec says clients SHOULD) rejects a
/// perfectly correct answer, and the failure lands on the agent as a broken
/// tool rather than as a manifest problem.
///
/// So nulls are written. The rule that follows, and that
/// <c>ToolManifestTests</c> pins: a property may be omitted from a response
/// only when its schema says it is optional — which, for these records,
/// means it carries a C# default value. Optional properties opt back out
/// with <c>[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]</c>
/// so the compact shapes stay compact.
/// </summary>
internal static class ToolSerialization
{
    internal static readonly JsonSerializerOptions Options =
        new(McpJsonUtilities.DefaultOptions)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
}
