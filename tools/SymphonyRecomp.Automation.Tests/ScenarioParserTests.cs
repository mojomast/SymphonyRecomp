using System.Text.Json;
using System.Text.Json.Nodes;
using SymphonyRecomp.Mcp.Scenarios;

namespace SymphonyRecomp.Automation.Tests;

public sealed class ScenarioParserTests
{
    private const string Valid = """
        {
          "schema": "sotn-scenario/1",
          "id": "coop-locomotion",
          "version": "1.0.0",
          "description": "Move both players and verify typed state.",
          "modId": "coop",
          "timeoutMs": 30000,
          "start": {
            "timeoutFrames": 300,
            "predicates": [
              { "type": "game", "field": "state", "equals": "Play" },
              { "type": "game", "field": "playerHasControl", "equals": true },
              { "type": "diagnostic", "schema": "p2d4/1", "field": "M", "result": "W" }
            ]
          },
          "steps": [
            {
              "id": "move",
              "inputs": [
                { "port": 0, "timeline": [{ "buttons": ["Right"], "frames": 30 }] },
                { "port": 1, "timeline": [{ "buttons": ["Cross", "Right"], "frames": 20 }, { "buttons": [], "frames": 10 }] }
              ],
              "checkpoint": {
                "timeoutFrames": 600,
                "predicates": [
                  { "type": "game", "field": "stage", "equals": "CEN" },
                  { "type": "game", "field": "gameStepRaw", "equals": 2 },
                  { "type": "diagnostic", "schema": "p2d4/1", "field": "M", "result": "P" },
                  { "type": "diagnostic", "schema": "p2d4/1", "field": "VER", "equals": "0.4.0" }
                ]
              }
            }
          ],
          "artifacts": {
            "onFailure": ["state", "diagnostics", "logs", "screenshot"],
            "onSuccess": ["state"]
          }
        }
        """;

    [Fact]
    public void CanonicalScenarioProducesTypedPredicatesAndButtonMasks()
    {
        ScenarioDefinition scenario = ScenarioParser.Parse(Valid);

        Assert.Equal(ScenarioParser.Schema, scenario.Schema);
        Assert.Equal(60, scenario.TotalInputFrames);
        Assert.Equal((ushort)0x2000, scenario.Steps[0].Inputs[0].Timeline[0].Buttons);
        Assert.Equal((ushort)0x2040, scenario.Steps[0].Inputs[1].Timeline[0].Buttons);
        var game = Assert.IsType<GameScenarioPredicate>(scenario.Start.Predicates[1]);
        Assert.Equal(GamePredicateField.PlayerHasControl, game.Field);
        Assert.True(game.Expected.Boolean);
        var diagnostic = Assert.IsType<DiagnosticScenarioPredicate>(scenario.Steps[0].Checkpoint.Predicates[2]);
        Assert.Equal(DiagnosticResult.Pass, diagnostic.Result);
    }

    [Theory]
    [InlineData("schema", "wrong/1")]
    [InlineData("id", "bad/id")]
    [InlineData("description", "")]
    [InlineData("timeoutMs", 120001)]
    public void InvalidRequiredRootValuesAreRejected(string property, object value)
    {
        JsonObject root = ParseRoot();
        root[property] = JsonValue.Create(value);
        Assert.Throws<FormatException>(() => ScenarioParser.Parse(root.ToJsonString()));
    }

    [Fact]
    public void CommentsTrailingCommasAndCaseInsensitiveDuplicatesAreRejected()
    {
        Assert.Throws<FormatException>(() => ScenarioParser.Parse(Valid.Replace("{", "{/*comment*/", StringComparison.Ordinal)));
        string trailingComma = Valid[..Valid.LastIndexOf('}')] + ",}";
        Assert.Throws<FormatException>(() => ScenarioParser.Parse(trailingComma));
        Assert.Throws<FormatException>(() => ScenarioParser.Parse(Valid.Replace("\"schema\":", "\"Schema\": \"sotn-scenario/1\", \"schema\":", StringComparison.Ordinal)));
    }

    [Fact]
    public void UnknownAndMissingPropertiesAreRejectedAtNestedLevels()
    {
        JsonObject unknown = ParseRoot();
        unknown["extra"] = true;
        Assert.Throws<FormatException>(() => ScenarioParser.Parse(unknown.ToJsonString()));

        JsonObject missing = ParseRoot();
        ((JsonObject)((JsonArray)missing["steps"]!)[0]!["checkpoint"]!).Remove("predicates");
        Assert.Throws<FormatException>(() => ScenarioParser.Parse(missing.ToJsonString()));
    }

    [Fact]
    public void SourceAndPrintableStringBoundsAreEnforced()
    {
        Assert.Throws<FormatException>(() => ScenarioParser.Parse(new string(' ', ScenarioParser.MaximumSourceBytes + 1)));
        JsonObject root = ParseRoot();
        root["description"] = "line\nbreak";
        Assert.Throws<FormatException>(() => ScenarioParser.Parse(root.ToJsonString()));
    }

    [Fact]
    public void StepInputAndPortBoundsAreEnforced()
    {
        JsonObject root = ParseRoot();
        JsonObject step = (JsonObject)((JsonArray)root["steps"]!)[0]!;
        JsonArray inputs = (JsonArray)step["inputs"]!;
        inputs.Add(inputs[0]!.DeepClone());
        Assert.Throws<FormatException>(() => ScenarioParser.Parse(root.ToJsonString()));

        root = ParseRoot();
        inputs = (JsonArray)((JsonObject)((JsonArray)root["steps"]!)[0]!)["inputs"]!;
        inputs[1]!["port"] = 0;
        Assert.Throws<FormatException>(() => ScenarioParser.Parse(root.ToJsonString()));

        root = ParseRoot();
        inputs = (JsonArray)((JsonObject)((JsonArray)root["steps"]!)[0]!)["inputs"]!;
        inputs[0]!["port"] = 2;
        Assert.Throws<FormatException>(() => ScenarioParser.Parse(root.ToJsonString()));
    }

    [Fact]
    public void TimelineSegmentRequestAndScenarioFrameBoundsAreEnforced()
    {
        JsonObject root = ParseRoot();
        JsonArray timeline = (JsonArray)((JsonArray)((JsonObject)((JsonArray)root["steps"]!)[0]!)["inputs"]!)[0]!["timeline"]!;
        timeline[0]!["frames"] = 1800;
        timeline.Add(JsonNode.Parse("{\"buttons\":[],\"frames\":1}"));
        Assert.Throws<FormatException>(() => ScenarioParser.Parse(root.ToJsonString()));

        root = ParseRoot();
        JsonArray steps = (JsonArray)root["steps"]!;
        JsonObject template = (JsonObject)steps[0]!;
        foreach (JsonNode? input in (JsonArray)template["inputs"]!)
        {
            JsonArray inputTimeline = (JsonArray)input!["timeline"]!;
            inputTimeline.Clear();
            inputTimeline.Add(JsonNode.Parse("{\"buttons\":[],\"frames\":1800}"));
        }
        JsonObject second = (JsonObject)template.DeepClone();
        second["id"] = "second";
        ((JsonArray)second["inputs"]!).RemoveAt(1);
        ((JsonArray)((JsonArray)second["inputs"]!)[0]!["timeline"]!).Clear();
        ((JsonArray)((JsonArray)second["inputs"]!)[0]!["timeline"]!).Add(JsonNode.Parse("{\"buttons\":[],\"frames\":1}"));
        steps.Add(second);
        Assert.Throws<FormatException>(() => ScenarioParser.Parse(root.ToJsonString()));
    }

    [Fact]
    public void TimelineAndStepCountBoundsAreEnforced()
    {
        JsonObject root = ParseRoot();
        JsonArray timeline = (JsonArray)((JsonArray)((JsonObject)((JsonArray)root["steps"]!)[0]!)["inputs"]!)[0]!["timeline"]!;
        while (timeline.Count < 121) timeline.Add(JsonNode.Parse("{\"buttons\":[],\"frames\":1}"));
        Assert.Throws<FormatException>(() => ScenarioParser.Parse(root.ToJsonString()));

        root = ParseRoot();
        JsonArray steps = (JsonArray)root["steps"]!;
        JsonObject template = (JsonObject)steps[0]!;
        ((JsonArray)template["inputs"]!).RemoveAt(1);
        ((JsonArray)((JsonArray)template["inputs"]!)[0]!["timeline"]!).Clear();
        ((JsonArray)((JsonArray)template["inputs"]!)[0]!["timeline"]!).Add(JsonNode.Parse("{\"buttons\":[],\"frames\":1}"));
        for (int index = 1; index < 33; index++)
        {
            JsonObject step = (JsonObject)template.DeepClone();
            step["id"] = $"step-{index}";
            steps.Add(step);
        }
        Assert.Throws<FormatException>(() => ScenarioParser.Parse(root.ToJsonString()));
    }

    [Theory]
    [InlineData("[\"Left\",\"Right\"]")]
    [InlineData("[\"Up\",\"Down\"]")]
    [InlineData("[\"Cross\",\"cross\"]")]
    [InlineData("[\"Turbo\"]")]
    public void InvalidButtonSetsAreRejected(string buttons)
    {
        JsonObject root = ParseRoot();
        JsonObject segment = (JsonObject)((JsonArray)((JsonArray)((JsonObject)((JsonArray)root["steps"]!)[0]!)["inputs"]!)[0]!["timeline"]!)[0]!;
        segment["buttons"] = JsonNode.Parse(buttons);
        Assert.Throws<FormatException>(() => ScenarioParser.Parse(root.ToJsonString()));
    }

    [Fact]
    public void CheckpointAndAggregatePredicateBoundsAreEnforced()
    {
        JsonObject root = ParseRoot();
        root["start"]!["timeoutFrames"] = 0;
        Assert.Throws<FormatException>(() => ScenarioParser.Parse(root.ToJsonString()));

        root = ParseRoot();
        JsonArray predicates = (JsonArray)root["start"]!["predicates"]!;
        while (predicates.Count < 17) predicates.Add(predicates[0]!.DeepClone());
        Assert.Throws<FormatException>(() => ScenarioParser.Parse(root.ToJsonString()));

        root = ParseRoot();
        predicates = (JsonArray)root["start"]!["predicates"]!;
        while (predicates.Count < 16) predicates.Add(predicates[0]!.DeepClone());
        JsonArray steps = (JsonArray)root["steps"]!;
        JsonObject template = (JsonObject)steps[0]!;
        ((JsonArray)template["inputs"]!).RemoveAt(1);
        ((JsonArray)((JsonArray)template["inputs"]!)[0]!["timeline"]!).Clear();
        ((JsonArray)((JsonArray)template["inputs"]!)[0]!["timeline"]!).Add(JsonNode.Parse("{\"buttons\":[],\"frames\":1}"));
        JsonArray checkpointPredicates = (JsonArray)template["checkpoint"]!["predicates"]!;
        while (checkpointPredicates.Count < 16) checkpointPredicates.Add(checkpointPredicates[0]!.DeepClone());
        for (int index = 1; index < 8; index++)
        {
            JsonObject step = (JsonObject)template.DeepClone();
            step["id"] = $"aggregate-{index}";
            steps.Add(step);
        }
        Assert.Throws<FormatException>(() => ScenarioParser.Parse(root.ToJsonString()));
    }

    [Theory]
    [InlineData("unknown", "Play")]
    [InlineData("loading", "false")]
    [InlineData("engineStepRaw", -1)]
    public void GamePredicatesRequireKnownFieldsAndTypedValues(string field, object equals)
    {
        JsonObject root = ParseRoot();
        JsonObject predicate = (JsonObject)((JsonArray)root["start"]!["predicates"]!)[0]!;
        predicate["field"] = field;
        predicate["equals"] = JsonValue.Create(equals);
        Assert.Throws<FormatException>(() => ScenarioParser.Parse(root.ToJsonString()));
    }

    [Fact]
    public void DiagnosticPredicatesRequireExactModeAndKnownResultStatus()
    {
        JsonObject root = ParseRoot();
        JsonObject predicate = (JsonObject)((JsonArray)root["start"]!["predicates"]!)[2]!;
        predicate["equals"] = "anything";
        Assert.Throws<FormatException>(() => ScenarioParser.Parse(root.ToJsonString()));

        root = ParseRoot();
        predicate = (JsonObject)((JsonArray)root["start"]!["predicates"]!)[2]!;
        predicate["result"] = "PASS";
        Assert.Throws<FormatException>(() => ScenarioParser.Parse(root.ToJsonString()));
    }

    [Fact]
    public void ArtifactPolicyIsClosedAndUnique()
    {
        JsonObject root = ParseRoot();
        ((JsonArray)root["artifacts"]!["onFailure"]!).Add("memory");
        Assert.Throws<FormatException>(() => ScenarioParser.Parse(root.ToJsonString()));

        root = ParseRoot();
        ((JsonArray)root["artifacts"]!["onSuccess"]!).Add("state");
        Assert.Throws<FormatException>(() => ScenarioParser.Parse(root.ToJsonString()));
    }

    private static JsonObject ParseRoot() => JsonNode.Parse(Valid)!.AsObject();
}
