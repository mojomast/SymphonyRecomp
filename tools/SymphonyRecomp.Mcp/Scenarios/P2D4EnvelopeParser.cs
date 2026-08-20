using System.Text;
using System.Text.Json;
using SymphonyRecomp.Automation.Contracts;

namespace SymphonyRecomp.Mcp.Scenarios;

internal sealed record P2D4Metric(MetricScalarType Type, long? Integer, bool? Boolean,
    string? String)
{
    public string Display => Integer?.ToString() ?? Boolean?.ToString().ToLowerInvariant() ?? String ?? "null";
}

internal sealed record P2D4Envelope(string Schema, IReadOnlyDictionary<string, string> Fields,
    IReadOnlyDictionary<string, P2D4Metric> Metrics, ScenarioDiagnosticIdentity Identity,
    string ModVersion, long ModFrame);

// One strict parser is shared by scenarios and campaigns. The transport already bounds its DTO,
// but this independently bounds and closes the producer-controlled p2d4/2 payload.
internal static class P2D4EnvelopeParser
{
    public const int MaximumUtf8Bytes = 64 * 1024;
    private static readonly string[] RootProperties =
    ["schema", "modVersion", "sessionId", "generation", "modFrame", "automationFrame", "legacy", "fields", "metrics"];
    private static readonly string[] FieldNames =
    ["VER", "H", "I", "K", "M", "R", "N", "B", "C", "T", "S", "G", "Q", "A", "E",
     "D", "VIS", "J", "X", "EN", "AW", "HU", "HP"];

    public static P2D4Envelope Parse(ModDiagnosticsDto diagnostics, string modId)
    {
        if (diagnostics.Id != modId || diagnostics.Frame < 0 || diagnostics.Generation < 0 ||
            diagnostics.SessionId.Length != 32 || diagnostics.SessionId.Any(value => !IsHex(value)) ||
            diagnostics.Payload.ValueKind != JsonValueKind.Object ||
            Encoding.UTF8.GetByteCount(diagnostics.Payload.GetRawText()) > MaximumUtf8Bytes)
            throw Invalid("Diagnostic identity, payload type, or size is invalid.");

        JsonElement root = diagnostics.Payload;
        RequireExactProperties(root, RootProperties, "envelope");
        string schema = Printable(root.GetProperty("schema"), 32, false, "schema");
        if (schema != "p2d4/2") throw Invalid("Diagnostic schema must be exactly p2d4/2.");
        string modVersion = Printable(root.GetProperty("modVersion"), 64, false, "modVersion");
        string session = Printable(root.GetProperty("sessionId"), 32, false, "sessionId");
        if (session != diagnostics.SessionId) throw Invalid("Payload session does not match transport session.");
        if (!root.GetProperty("generation").TryGetInt32(out int generation) || generation != diagnostics.Generation)
            throw Invalid("Payload generation does not match transport generation.");
        if (!root.GetProperty("automationFrame").TryGetInt64(out long frame) || frame != diagnostics.Frame)
            throw Invalid("Payload frame does not match transport frame.");
        if (!root.GetProperty("modFrame").TryGetInt64(out long modFrame) || modFrame < 0)
            throw Invalid("Mod frame is invalid.");
        string legacy = Printable(root.GetProperty("legacy"), 16 * 1024, false, "legacy");
        if (!legacy.StartsWith("P2D4 ", StringComparison.Ordinal))
            throw Invalid("Diagnostic legacy report is not P2D4.");

        JsonElement fieldsElement = root.GetProperty("fields");
        if (fieldsElement.ValueKind != JsonValueKind.Object) throw Invalid("Diagnostic fields must be an object.");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty field in fieldsElement.EnumerateObject())
        {
            if (!SafeName(field.Name, 64) || !fields.TryAdd(field.Name,
                Printable(field.Value, 16 * 1024, true, $"field {field.Name}")))
                throw Invalid("Diagnostic fields contain an invalid or duplicate name.");
        }
        if (fields.Count != FieldNames.Length || FieldNames.Any(name => !fields.ContainsKey(name)) ||
            fields["VER"] != modVersion)
            throw Invalid("Diagnostic fields do not match the exact P2D4 contract.");

        JsonElement metricsElement = root.GetProperty("metrics");
        if (metricsElement.ValueKind != JsonValueKind.Object) throw Invalid("Diagnostic metrics must be an object.");
        var metrics = new Dictionary<string, P2D4Metric>(StringComparer.Ordinal);
        foreach (JsonProperty metric in metricsElement.EnumerateObject())
        {
            if (!ScenarioMetricContract.Types.TryGetValue(metric.Name, out MetricScalarType expected) ||
                metrics.ContainsKey(metric.Name))
                throw Invalid("Diagnostic metrics contain an unknown or duplicate name.");
            P2D4Metric parsed = ParseMetric(metric.Value);
            if (parsed.Type != expected) throw Invalid("Diagnostic metric scalar type does not match its contract.");
            metrics.Add(metric.Name, parsed);
        }
        if (metrics.Count != ScenarioMetricContract.Types.Count)
            throw Invalid("Diagnostic metrics are incomplete.");

        return new(schema, fields, metrics,
            new ScenarioDiagnosticIdentity(session, generation, frame, schema), modVersion, modFrame);
    }

    private static P2D4Metric ParseMetric(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt64(out long integer) =>
            new(MetricScalarType.Integer, integer, null, null),
        JsonValueKind.True => new(MetricScalarType.Boolean, null, true, null),
        JsonValueKind.False => new(MetricScalarType.Boolean, null, false, null),
        JsonValueKind.String => new(MetricScalarType.String, null, null,
            Printable(value, 256, true, "metric string")),
        _ => throw Invalid("Diagnostic metric must be an integer, boolean, or bounded printable string."),
    };

    private static void RequireExactProperties(JsonElement value, string[] expected, string name)
    {
        string[] actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != expected.Length || actual.Distinct(StringComparer.Ordinal).Count() != actual.Length ||
            actual.Except(expected, StringComparer.Ordinal).Any())
            throw Invalid($"Diagnostic {name} property set is not exact.");
    }

    private static string Printable(JsonElement value, int maximum, bool allowEmpty, string name)
    {
        if (value.ValueKind != JsonValueKind.String) throw Invalid($"Diagnostic {name} must be a string.");
        string text = value.GetString()!;
        if ((!allowEmpty && text.Length == 0) || text.Length > maximum ||
            text.Any(character => character is < ' ' or > '~'))
            throw Invalid($"Diagnostic {name} is not bounded printable ASCII.");
        return text;
    }

    private static bool SafeName(string value, int maximum) => value.Length is > 0 && value.Length <= maximum &&
        value[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' &&
        value.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
    private static bool IsHex(char value) => value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
    private static InvalidDataException Invalid(string message) => new(message);
}
