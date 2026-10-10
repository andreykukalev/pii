using System.Text.Json.Nodes;

namespace Pii.Application;

public static class ResearchSchemas
{
    private static JsonObject String(params string[] values)
    {
        var schema = new JsonObject { ["type"] = "string" };
        if (values.Length > 0) schema["enum"] = new JsonArray(values.Select(item => JsonValue.Create(item) as JsonNode).ToArray());
        return schema;
    }
    private static JsonObject NullableString() => new() { ["type"] = new JsonArray("string", "null") };
    private static JsonObject Array(JsonNode item) => new() { ["type"] = "array", ["items"] = item };
    private static JsonObject Object(params (string Name, JsonNode Schema)[] fields)
    {
        var properties = new JsonObject();
        foreach (var field in fields) properties[field.Name] = field.Schema;
        return new() { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = properties,
            ["required"] = new JsonArray(fields.Select(field => JsonValue.Create(field.Name) as JsonNode).ToArray()) };
    }
    public static readonly string ToolArguments = Object(("instrumentId", String()), ("question", String())).ToJsonString();
    public static readonly string Decision = Object(("action", String("ProposeFinish", "StopPartial")), ("target", String()),
        ("instrumentId", String()), ("question", String()), ("rationale", String())).ToJsonString();
    public static readonly string Output = Object(
        ("claims", Array(Object(("id", String()), ("instrumentId", String()), ("horizon", String("LongTerm", "Tactical")),
            ("kind", String("SourcedFact", "DerivedFact", "Interpretation", "Hypothesis", "Unresolved")), ("text", String()),
            ("evidenceIds", Array(String())), ("numericReference", NullableString()), ("counterEvidenceIds", Array(String()))))),
        ("assessments", Array(Object(("instrumentId", String()), ("horizon", String("LongTerm", "Tactical")),
            ("category", String("BUY", "HOLD", "REDUCE", "SELL", "REVIEW", "WATCHLIST")), ("thesis", String()),
            ("counterargument", String()), ("claimIds", Array(String())), ("risks", Array(String()))))),
        ("followUps", Array(Object(("instrumentId", String()), ("question", String()), ("reason", String())))),
        ("uncertainties", Array(String())), ("status", String("Succeeded", "Partial", "Unavailable"))).ToJsonString();
}