using System.Text;
using System.Text.Json;
using SymphonyRecomp.Automation.Contracts;

namespace SymphonyRecomp.Mcp.Scenarios;

public static class ScenarioParser
{
    public const string Schema = "sotn-scenario/1";
    public const int MaximumSourceBytes = 256 * 1024;

    private static readonly IReadOnlyDictionary<string, ushort> ButtonBits =
        new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["L2"] = 0x0001, ["R2"] = 0x0002, ["L1"] = 0x0004, ["R1"] = 0x0008,
            ["Triangle"] = 0x0010, ["Circle"] = 0x0020, ["Cross"] = 0x0040, ["Square"] = 0x0080,
            ["Select"] = 0x0100, ["L3"] = 0x0200, ["R3"] = 0x0400, ["Start"] = 0x0800,
            ["Up"] = 0x1000, ["Right"] = 0x2000, ["Down"] = 0x4000, ["Left"] = 0x8000,
        };

    public static ScenarioDefinition Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Encoding.UTF8.GetByteCount(source) > MaximumSourceBytes)
            throw new FormatException($"Scenario source exceeds {MaximumSourceBytes} UTF-8 bytes.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(source, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
        }
        catch (JsonException exception)
        {
            throw new FormatException("Scenario source is not strict JSON.", exception);
        }

        using (document)
        {
            RejectDuplicateProperties(document.RootElement, "$");
            JsonElement root = RequireObject(document.RootElement, "$");
            RequireProperties(root, "$", ["schema", "id", "version", "description", "modId", "timeoutMs", "start", "steps", "artifacts"]);

            string schema = RequireString(root, "schema", "$", 32);
            if (schema != Schema) throw new FormatException($"$.schema must be exactly '{Schema}'.");
            string id = RequireId(root, "id", "$", 64);
            string version = RequireId(root, "version", "$", 32);
            string description = RequirePrintable(root, "description", "$", 512, allowEmpty: false);
            string modId = RequireId(root, "modId", "$", 128);
            int timeoutMs = RequireInt32(root, "timeoutMs", "$", 1, 120000);
            ScenarioCheckpoint start = ParseCheckpoint(Required(root, "start", "$"), "$.start");

            JsonElement stepsElement = RequireArray(Required(root, "steps", "$"), "$.steps", 1, 32);
            var steps = new ScenarioStep[stepsElement.GetArrayLength()];
            var stepIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int totalPredicates = start.Predicates.Count;
            int totalInputFrames = 0;
            int stepIndex = 0;
            foreach (JsonElement element in stepsElement.EnumerateArray())
            {
                string path = $"$.steps[{stepIndex}]";
                ScenarioStep step = ParseStep(element, path, ref totalInputFrames);
                if (!stepIds.Add(step.Id)) throw new FormatException($"{path}.id is duplicated.");
                totalPredicates += step.Checkpoint.Predicates.Count;
                if (totalPredicates > 128) throw new FormatException("Scenario checkpoints cannot contain more than 128 predicates total.");
                steps[stepIndex++] = step;
            }

            ScenarioArtifactPolicy artifacts = ParseArtifacts(Required(root, "artifacts", "$"), "$.artifacts");
            return new ScenarioDefinition(schema, id, version, description, modId, timeoutMs, start, steps, artifacts, totalInputFrames);
        }
    }

    private static ScenarioStep ParseStep(JsonElement element, string path, ref int totalInputFrames)
    {
        JsonElement value = RequireObject(element, path);
        RequireProperties(value, path, ["id", "inputs", "checkpoint"]);
        string id = RequireId(value, "id", path, 64);
        JsonElement inputsElement = RequireArray(Required(value, "inputs", path), $"{path}.inputs", 1, 2);
        var inputs = new ScenarioInput[inputsElement.GetArrayLength()];
        var ports = new HashSet<int>();
        int index = 0;
        foreach (JsonElement inputElement in inputsElement.EnumerateArray())
        {
            string inputPath = $"{path}.inputs[{index}]";
            ScenarioInput input = ParseInput(inputElement, inputPath, ref totalInputFrames);
            if (!ports.Add(input.Port)) throw new FormatException($"{path}.inputs contains duplicate port {input.Port}.");
            inputs[index++] = input;
        }

        ScenarioCheckpoint checkpoint = ParseCheckpoint(Required(value, "checkpoint", path), $"{path}.checkpoint");
        return new ScenarioStep(id, inputs, checkpoint);
    }

    private static ScenarioInput ParseInput(JsonElement element, string path, ref int totalInputFrames)
    {
        JsonElement value = RequireObject(element, path);
        RequireProperties(value, path, ["port", "timeline"]);
        int port = RequireInt32(value, "port", path, 0, 1);
        JsonElement timeline = RequireArray(Required(value, "timeline", path), $"{path}.timeline", 1, 120);
        var segments = new InputSegmentDto[timeline.GetArrayLength()];
        int requestFrames = 0;
        int index = 0;
        foreach (JsonElement segmentElement in timeline.EnumerateArray())
        {
            string segmentPath = $"{path}.timeline[{index}]";
            JsonElement segment = RequireObject(segmentElement, segmentPath);
            RequireProperties(segment, segmentPath, ["buttons", "frames"]);
            ushort buttons = ParseButtons(Required(segment, "buttons", segmentPath), $"{segmentPath}.buttons");
            int frames = RequireInt32(segment, "frames", segmentPath, 1, 1800);
            requestFrames = checked(requestFrames + frames);
            if (requestFrames > 1800) throw new FormatException($"{path}.timeline cannot exceed 1800 frames.");
            totalInputFrames = checked(totalInputFrames + frames);
            if (totalInputFrames > 3600) throw new FormatException("Scenario input cannot exceed 3600 frames total.");
            segments[index++] = new InputSegmentDto(buttons, frames);
        }
        return new ScenarioInput(port, segments, requestFrames);
    }

    private static ushort ParseButtons(JsonElement element, string path)
    {
        JsonElement buttons = RequireArray(element, path, 0, 16);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ushort mask = 0;
        int index = 0;
        foreach (JsonElement buttonElement in buttons.EnumerateArray())
        {
            if (buttonElement.ValueKind != JsonValueKind.String)
                throw new FormatException($"{path}[{index}] must be a button name.");
            string? button = buttonElement.GetString();
            if (button == null || !ButtonBits.TryGetValue(button, out ushort bit))
                throw new FormatException($"{path}[{index}] is not a known button name.");
            if (!seen.Add(button)) throw new FormatException($"{path} contains a duplicate button name.");
            mask |= bit;
            index++;
        }
        if ((mask & 0xA000) == 0xA000) throw new FormatException($"{path} cannot press Left and Right together.");
        if ((mask & 0x5000) == 0x5000) throw new FormatException($"{path} cannot press Up and Down together.");
        return mask;
    }

    private static ScenarioCheckpoint ParseCheckpoint(JsonElement element, string path)
    {
        JsonElement value = RequireObject(element, path);
        RequireProperties(value, path, ["timeoutFrames", "predicates"]);
        int timeoutFrames = RequireInt32(value, "timeoutFrames", path, 1, 1800);
        JsonElement predicateElements = RequireArray(Required(value, "predicates", path), $"{path}.predicates", 1, 16);
        var predicates = new ScenarioPredicate[predicateElements.GetArrayLength()];
        int index = 0;
        foreach (JsonElement predicate in predicateElements.EnumerateArray())
            predicates[index] = ParsePredicate(predicate, $"{path}.predicates[{index++}]");
        return new ScenarioCheckpoint(timeoutFrames, predicates);
    }

    private static ScenarioPredicate ParsePredicate(JsonElement element, string path)
    {
        JsonElement value = RequireObject(element, path);
        JsonElement typeElement = Required(value, "type", path);
        if (typeElement.ValueKind != JsonValueKind.String) throw new FormatException($"{path}.type must be a string.");
        return typeElement.GetString() switch
        {
            "game" => ParseGamePredicate(value, path),
            "diagnostic" => ParseDiagnosticPredicate(value, path),
            _ => throw new FormatException($"{path}.type must be 'game' or 'diagnostic'."),
        };
    }

    private static GameScenarioPredicate ParseGamePredicate(JsonElement value, string path)
    {
        RequireProperties(value, path, ["type", "field", "equals"]);
        string fieldText = RequireString(value, "field", path, 32);
        GamePredicateField field = fieldText switch
        {
            "state" => GamePredicateField.State,
            "stage" => GamePredicateField.Stage,
            "character" => GamePredicateField.Character,
            "gameStepRaw" => GamePredicateField.GameStepRaw,
            "engineStepRaw" => GamePredicateField.EngineStepRaw,
            "loading" => GamePredicateField.Loading,
            "menuOpen" => GamePredicateField.MenuOpen,
            "mapOpen" => GamePredicateField.MapOpen,
            "playerHasControl" => GamePredicateField.PlayerHasControl,
            _ => throw new FormatException($"{path}.field is not a supported game field."),
        };
        JsonElement equals = Required(value, "equals", path);
        ScenarioScalar scalar = field switch
        {
            GamePredicateField.State or GamePredicateField.Stage or GamePredicateField.Character =>
                new ScenarioScalar(RequirePrintableString(equals, $"{path}.equals", 64, false), null, null),
            GamePredicateField.GameStepRaw or GamePredicateField.EngineStepRaw =>
                new ScenarioScalar(null, RequireUInt32(equals, $"{path}.equals"), null),
            _ => new ScenarioScalar(null, null, RequireBoolean(equals, $"{path}.equals")),
        };
        return new GameScenarioPredicate(field, scalar);
    }

    private static DiagnosticScenarioPredicate ParseDiagnosticPredicate(JsonElement value, string path)
    {
        bool hasEquals = value.TryGetProperty("equals", out JsonElement equalsElement);
        bool hasResult = value.TryGetProperty("result", out JsonElement resultElement);
        if (hasEquals == hasResult) throw new FormatException($"{path} must contain exactly one of equals or result.");
        RequireProperties(value, path, hasEquals
            ? ["type", "schema", "field", "equals"]
            : ["type", "schema", "field", "result"]);
        string schema = RequirePrintable(value, "schema", path, 64, allowEmpty: false);
        string field = RequireId(value, "field", path, 64);
        if (hasEquals)
        {
            string equals = RequirePrintableString(equalsElement, $"{path}.equals", 256, allowEmpty: true);
            return new DiagnosticScenarioPredicate(schema, field, equals, null);
        }

        if (resultElement.ValueKind != JsonValueKind.String) throw new FormatException($"{path}.result must be P, W, or F.");
        DiagnosticResult result = resultElement.GetString() switch
        {
            "P" => DiagnosticResult.Pass,
            "W" => DiagnosticResult.Wait,
            "F" => DiagnosticResult.Fail,
            _ => throw new FormatException($"{path}.result must be P, W, or F."),
        };
        return new DiagnosticScenarioPredicate(schema, field, null, result);
    }

    private static ScenarioArtifactPolicy ParseArtifacts(JsonElement element, string path)
    {
        JsonElement value = RequireObject(element, path);
        RequireProperties(value, path, ["onFailure", "onSuccess"]);
        return new ScenarioArtifactPolicy(
            ParseArtifactList(Required(value, "onFailure", path), $"{path}.onFailure"),
            ParseArtifactList(Required(value, "onSuccess", path), $"{path}.onSuccess"));
    }

    private static ScenarioArtifact[] ParseArtifactList(JsonElement element, string path)
    {
        JsonElement value = RequireArray(element, path, 0, 5);
        var artifacts = new ScenarioArtifact[value.GetArrayLength()];
        var seen = new HashSet<ScenarioArtifact>();
        int index = 0;
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) throw new FormatException($"{path}[{index}] must be an artifact name.");
            ScenarioArtifact artifact = item.GetString() switch
            {
                "state" => ScenarioArtifact.State,
                "diagnostics" => ScenarioArtifact.Diagnostics,
                "entities" => ScenarioArtifact.Entities,
                "logs" => ScenarioArtifact.Logs,
                "screenshot" => ScenarioArtifact.Screenshot,
                _ => throw new FormatException($"{path}[{index}] is not a supported artifact."),
            };
            if (!seen.Add(artifact)) throw new FormatException($"{path} contains a duplicate artifact.");
            artifacts[index++] = artifact;
        }
        return artifacts;
    }

    private static void RejectDuplicateProperties(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new FormatException($"{path} contains duplicate property '{property.Name}'.");
                RejectDuplicateProperties(property.Value, $"{path}.{property.Name}");
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in element.EnumerateArray()) RejectDuplicateProperties(item, $"{path}[{index++}]");
        }
    }

    private static void RequireProperties(JsonElement value, string path, string[] expected)
    {
        var names = new HashSet<string>(value.EnumerateObject().Select(property => property.Name), StringComparer.Ordinal);
        foreach (string name in expected)
            if (!names.Remove(name)) throw new FormatException($"{path}.{name} is required.");
        if (names.Count != 0) throw new FormatException($"{path} contains unknown property '{names.Order().First()}'.");
    }

    private static JsonElement Required(JsonElement value, string name, string path) =>
        value.TryGetProperty(name, out JsonElement property)
            ? property
            : throw new FormatException($"{path}.{name} is required.");

    private static JsonElement RequireObject(JsonElement value, string path) => value.ValueKind == JsonValueKind.Object
        ? value
        : throw new FormatException($"{path} must be an object.");

    private static JsonElement RequireArray(JsonElement value, string path, int minimum, int maximum)
    {
        if (value.ValueKind != JsonValueKind.Array) throw new FormatException($"{path} must be an array.");
        int count = value.GetArrayLength();
        if (count < minimum || count > maximum) throw new FormatException($"{path} must contain {minimum} to {maximum} entries.");
        return value;
    }

    private static string RequireString(JsonElement value, string name, string path, int maximum) =>
        RequirePrintableString(Required(value, name, path), $"{path}.{name}", maximum, allowEmpty: false);

    private static string RequirePrintable(JsonElement value, string name, string path, int maximum, bool allowEmpty) =>
        RequirePrintableString(Required(value, name, path), $"{path}.{name}", maximum, allowEmpty);

    private static string RequirePrintableString(JsonElement value, string path, int maximum, bool allowEmpty)
    {
        if (value.ValueKind != JsonValueKind.String) throw new FormatException($"{path} must be a string.");
        string result = value.GetString()!;
        if ((!allowEmpty && result.Length == 0) || result.Length > maximum || result.Any(character => character is < ' ' or > '~'))
            throw new FormatException($"{path} must contain {(allowEmpty ? "zero to" : "1 to")} {maximum} printable ASCII characters.");
        return result;
    }

    private static string RequireId(JsonElement value, string name, string path, int maximum)
    {
        string result = RequireString(value, name, path, maximum);
        if (!IsAsciiAlphaNumeric(result[0]) || result.Any(character => !IsAsciiAlphaNumeric(character) && character is not ('.' or '_' or '-')))
            throw new FormatException($"{path}.{name} is not a safe ASCII identifier.");
        return result;
    }

    private static bool IsAsciiAlphaNumeric(char value) =>
        value is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static int RequireInt32(JsonElement value, string name, string path, int minimum, int maximum)
    {
        JsonElement property = Required(value, name, path);
        if (!property.TryGetInt32(out int result) || result < minimum || result > maximum)
            throw new FormatException($"{path}.{name} must be an integer from {minimum} through {maximum}.");
        return result;
    }

    private static uint RequireUInt32(JsonElement value, string path)
    {
        if (!value.TryGetUInt32(out uint result)) throw new FormatException($"{path} must be an unsigned 32-bit integer.");
        return result;
    }

    private static bool RequireBoolean(JsonElement value, string path) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new FormatException($"{path} must be a boolean."),
    };
}

public sealed record ScenarioDefinition(
    string Schema,
    string Id,
    string Version,
    string Description,
    string ModId,
    int TimeoutMs,
    ScenarioCheckpoint Start,
    IReadOnlyList<ScenarioStep> Steps,
    ScenarioArtifactPolicy Artifacts,
    int TotalInputFrames);

public sealed record ScenarioStep(string Id, IReadOnlyList<ScenarioInput> Inputs, ScenarioCheckpoint Checkpoint);
public sealed record ScenarioInput(int Port, IReadOnlyList<InputSegmentDto> Timeline, int TotalFrames);
public sealed record ScenarioCheckpoint(int TimeoutFrames, IReadOnlyList<ScenarioPredicate> Predicates);
public abstract record ScenarioPredicate;
public sealed record GameScenarioPredicate(GamePredicateField Field, ScenarioScalar Expected) : ScenarioPredicate;
public sealed record DiagnosticScenarioPredicate(string Schema, string Field, string? ExactValue, DiagnosticResult? Result) : ScenarioPredicate;
public sealed record ScenarioScalar(string? String, uint? UnsignedInteger, bool? Boolean);
public sealed record ScenarioArtifactPolicy(IReadOnlyList<ScenarioArtifact> OnFailure, IReadOnlyList<ScenarioArtifact> OnSuccess);

public enum GamePredicateField
{
    State,
    Stage,
    Character,
    GameStepRaw,
    EngineStepRaw,
    Loading,
    MenuOpen,
    MapOpen,
    PlayerHasControl,
}

public enum DiagnosticResult { Pass, Wait, Fail }
public enum ScenarioArtifact { State, Diagnostics, Entities, Logs, Screenshot }
