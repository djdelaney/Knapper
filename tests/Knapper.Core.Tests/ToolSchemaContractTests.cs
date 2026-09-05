using System.Text.Json;
using Knapper.Core;

namespace Knapper.Core.Tests;

/// <summary>
/// The predicate itself, pinned against the real 0.3.2 manifest entries —
/// the defect that blocked a cutover, and the well-formed neighbours it must
/// not accuse. A gate that cries wolf gets switched off, so the
/// false-positive cases are as load-bearing as the true one.
/// </summary>
public class ToolSchemaContractTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static readonly JsonElement AnyInput = Json("""{"type":"object","properties":{}}""");

    [Fact]
    public void The_0_3_2_vault_search_schema_is_rejected()
    {
        // Verbatim from the wire, 0.3.2+g9ebbc48. Legal draft 2020-12 — a
        // boolean IS a subschema, `true` meaning "anything validates" — and
        // useless: Claude Code rejected it and discarded all 13 tools.
        var problems = ToolSchemaContract.Validate(
            "vault_search",
            AnyInput,
            Json("""{"type":"object","properties":{"result":true},"required":["result"]}"""));

        problems.ShouldHaveSingleItem().ShouldContain("outputSchema.properties.result");
    }

    [Fact]
    public void A_bare_boolean_schema_is_rejected()
    {
        // The same defect one layer up: the SDK's in-process representation
        // of that tool, before the scalar-wrapping the transport applies.
        ToolSchemaContract.Validate("vault_search", AnyInput, Json("true"))
            .ShouldHaveSingleItem().ShouldContain("outputSchema is the boolean true");
    }

    [Fact]
    public void A_permissive_subschema_nested_below_the_root_is_rejected()
    {
        // A loosely typed MEMBER, which a top-level-only check cannot see.
        var problems = ToolSchemaContract.Validate("vault_files", AnyInput, Json("""
            {"type":"object","properties":{"items":{"type":"array","items":{"type":"object",
             "properties":{"path":{"type":"string"},"meta":true}}}}}
            """));

        problems.ShouldHaveSingleItem().ShouldContain("outputSchema.properties.items.items.properties.meta");
    }

    [Fact]
    public void Well_formed_schemas_pass_including_the_scalar_result_wrapper()
    {
        // vault_mkdir returns a string; the transport wraps it. Correct, and
        // a check that flagged it would be unusable.
        ToolSchemaContract.Validate(
            "vault_mkdir",
            AnyInput,
            Json("""{"type":"object","properties":{"result":{"type":"string"}},"required":["result"]}"""))
            .ShouldBeEmpty();

        // additionalProperties: false is a BOOLEAN in a non-subschema
        // position — ordinary, correct, and never a finding.
        ToolSchemaContract.Validate(
            "vault_stat",
            AnyInput,
            Json("""
                {"type":"object","additionalProperties":false,
                 "properties":{"path":{"type":"string"},"size":{"type":["integer","null"]}}}
                """))
            .ShouldBeEmpty();
    }

    [Fact]
    public void An_absent_output_schema_is_valid_but_an_absent_input_schema_is_not()
    {
        ToolSchemaContract.Validate("vault_stat", AnyInput, null).ShouldBeEmpty();
        ToolSchemaContract.Validate("vault_stat", null, null)
            .ShouldHaveSingleItem().ShouldContain("inputSchema is absent");
    }

    [Fact]
    public void A_response_missing_a_property_its_own_schema_requires_is_reported()
    {
        var schema = Json("""
            {"type":"object","required":["items","truncated","nextCursor"],
             "properties":{"items":{"type":"array","items":{"type":"object","required":["path"],
             "properties":{"path":{"type":"string"}}}},"truncated":{"type":"boolean"},
             "nextCursor":{"type":["string","null"]}}}
            """);

        // The serializer dropped a null nextCursor the schema requires.
        ToolSchemaContract.FindMissingRequired("vault_files", schema, Json("""
            {"items":[{"path":"a.md"}],"truncated":false}
            """))
            .ShouldHaveSingleItem().ShouldContain("result.nextCursor");

        // Nested, inside an array element.
        ToolSchemaContract.FindMissingRequired("vault_files", schema, Json("""
            {"items":[{"path":"a.md"},{}],"truncated":false,"nextCursor":null}
            """))
            .ShouldHaveSingleItem().ShouldContain("result.items[1].path");

        ToolSchemaContract.FindMissingRequired("vault_files", schema, Json("""
            {"items":[{"path":"a.md"}],"truncated":false,"nextCursor":null}
            """))
            .ShouldBeEmpty();
    }

    [Fact]
    public void A_tool_that_publishes_a_schema_and_returns_nothing_is_reported()
    {
        ToolSchemaContract.FindMissingRequired("vault_stat", Json("""{"type":"object"}"""), null)
            .ShouldHaveSingleItem().ShouldContain("no structured content");
    }

    /// <summary>
    /// The gate has to BITE, and its message has to show the operator where
    /// the client stopped reading — the whole failure mode is that a full
    /// string was sent and a partial one arrived, so "too long" alone repeats
    /// what is already invisible.
    /// </summary>
    [Fact]
    public void Text_over_the_budget_is_reported_with_the_words_the_client_stops_at()
    {
        ToolSchemaContract.FindOverBudgetText("vault_x description", new string('a', 1950)).ShouldBeEmpty();
        ToolSchemaContract.FindOverBudgetText("vault_x description", null).ShouldBeEmpty();

        var text = new string('a', 2038) + "STOPS HERE" + new string('b', 400);
        var problems = ToolSchemaContract.FindOverBudgetText("vault_x description", text);

        var problem = problems.ShouldHaveSingleItem();
        problem.ShouldContain("vault_x description");
        problem.ShouldContain("STOPS HERE");
        problem.ShouldNotContain("bbbb");
    }
}
