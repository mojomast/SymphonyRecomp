using ModelContextProtocol;
using SymphonyRecomp.Automation.Contracts;
using SymphonyRecomp.Mcp;
using SymphonyRecomp.Mcp.Scenarios;

namespace SymphonyRecomp.Automation.Tests;

public sealed class ScenarioCatalogTests
{
    [Fact]
    public void StableCatalogInventoryAndDescriptorFailuresAreClosed()
    {
        var catalog = new ScenarioCatalog();
        Assert.Equal(new[] { "coop-locomotion-jump", "coop-transition-west", "coop-contact-hit",
            "coop-projectile-hit", "coop-damage-revive", "coop-drop-observe" },
            catalog.Inventory.Select(value => value.Id));
        Assert.All(catalog.Inventory, entry => Assert.Equal(entry.Id, ScenarioParser.Parse(catalog.GetSource(entry.Id)).Id));

        string resource = catalog.Inventory[0].ResourceName;
        Assert.Throws<InvalidOperationException>(() => new ScenarioCatalog(typeof(ScenarioCatalog).Assembly,
            [new("duplicate", "1", resource), new("duplicate", "2", resource + ".other")]));
        Assert.Throws<InvalidOperationException>(() => new ScenarioCatalog(typeof(ScenarioCatalog).Assembly,
            [new("missing", "1", "missing.resource")]));
        Assert.Throws<InvalidOperationException>(() => new ScenarioCatalog(typeof(ScenarioCatalog).Assembly,
            [new("wrong-id", "2", resource)]));
    }

    [Fact]
    public void EmbeddedCanonicalSourceHasExactTimelinePredicatesAndPolicy()
    {
        var catalog = new ScenarioCatalog();

        ScenarioDefinition scenario = ScenarioParser.Parse(
            catalog.GetSource(ScenarioCatalog.CoopLocomotionJumpId));

        Assert.Equal("coop-locomotion-jump", scenario.Id);
        Assert.Equal("2", scenario.Version);
        Assert.Equal(ScenarioParser.SchemaV2, scenario.Schema);
        Assert.Equal(DiagnosticsResetPolicy.Before, scenario.DiagnosticsReset);
        Assert.Equal("coop-feasibility", scenario.ModId);
        Assert.Equal(144, scenario.TotalInputFrames);
        Assert.DoesNotContain(scenario.Start.Predicates.OfType<GameScenarioPredicate>(),
            predicate => predicate.Field == GamePredicateField.Stage);
        AssertGame(scenario.Start, GamePredicateField.State, text: "Play");
        AssertGame(scenario.Start, GamePredicateField.Character, text: "Alucard");
        AssertGame(scenario.Start, GamePredicateField.Loading, boolean: false);
        AssertGame(scenario.Start, GamePredicateField.MenuOpen, boolean: false);
        AssertGame(scenario.Start, GamePredicateField.MapOpen, boolean: false);
        Assert.DoesNotContain(scenario.Start.Predicates.OfType<GameScenarioPredicate>(),
            predicate => predicate.Field == GamePredicateField.PlayerHasControl);
        AssertDiagnostic(scenario.Start, "H", result: DiagnosticResult.Pass);
        AssertDiagnostic(scenario.Start, "K", exact: "-");
        AssertMetric(scenario.Start, "errorCode", MetricOperator.Eq);

        ScenarioStep step = Assert.Single(scenario.Steps);
        Assert.Equal("locomotion-jump", step.Id);
        ScenarioInput port0 = Assert.Single(step.Inputs, input => input.Port == 0);
        Assert.Equal(72, port0.TotalFrames);
        InputSegmentDto neutral = Assert.Single(port0.Timeline);
        Assert.Equal((ushort)0, neutral.Buttons);
        Assert.Equal(72, neutral.Frames);

        ScenarioInput port1 = Assert.Single(step.Inputs, input => input.Port == 1);
        Assert.Equal(72, port1.TotalFrames);
        Assert.Equal(new ushort[] { 0, 0x2000, 0, 0x8000, 0, 0x0040, 0 },
            port1.Timeline.Select(segment => segment.Buttons));
        Assert.Equal(new[] { 8, 12, 4, 12, 4, 2, 30 },
            port1.Timeline.Select(segment => segment.Frames));
        AssertDiagnostic(step.Checkpoint, "M", result: DiagnosticResult.Pass);
        AssertMetric(step.Checkpoint, "attackOrphanMarkerCount", MetricOperator.Eq);
        Assert.DoesNotContain(step.Checkpoint.Predicates.OfType<DiagnosticScenarioPredicate>(),
            predicate => predicate.Field == "J");
        Assert.Equal(new[] { ScenarioArtifact.State, ScenarioArtifact.Diagnostics,
            ScenarioArtifact.Entities, ScenarioArtifact.Logs, ScenarioArtifact.Screenshot },
            scenario.Artifacts.OnFailure);
        Assert.Equal(new[] { ScenarioArtifact.State, ScenarioArtifact.Diagnostics },
            scenario.Artifacts.OnSuccess);
    }

    [Fact]
    public void M5ProbesUseTelemetryStageNamesAndHonestTransitionTimelines()
    {
        var catalog = new ScenarioCatalog();
        foreach (string id in new[] { "coop-transition-west", "coop-contact-hit",
                     "coop-projectile-hit", "coop-damage-revive" })
        {
            ScenarioDefinition probe = ScenarioParser.Parse(catalog.GetSource(id));
            AssertGame(probe.Start, GamePredicateField.Stage, text: "MarbleGallery");
        }

        foreach (string id in new[] { "coop-contact-hit", "coop-projectile-hit" })
        {
            ScenarioDefinition probe = ScenarioParser.Parse(catalog.GetSource(id));
            AssertDiagnostic(probe.Start, "EN", result: DiagnosticResult.Pass);
        }

        ScenarioDefinition transition = ScenarioParser.Parse(catalog.GetSource("coop-transition-west"));
        Assert.Equal("5", transition.Version);
        Assert.Contains("no more than 8 walkable world pixels", transition.Description);
        AssertGame(transition.Start, GamePredicateField.RoomX, integer: 32);
        AssertGame(transition.Start, GamePredicateField.RoomY, integer: 27);
        ScenarioStep step = Assert.Single(transition.Steps);
        ScenarioInput p1 = Assert.Single(step.Inputs, input => input.Port == 0);
        ScenarioInput p2 = Assert.Single(step.Inputs, input => input.Port == 1);
        Assert.Equal(new[] { 8, 10, 50, 124 }, p1.Timeline.Select(value => value.Frames));
        Assert.Equal(new[] { 120, 8, 64 }, p2.Timeline.Select(value => value.Frames));
        Assert.Equal((ushort)0x8000, p2.Timeline[1].Buttons);
        AssertMetric(step.Checkpoint, "postTransitionCommandedPixels", MetricOperator.DeltaGte);
        AssertMetric(step.Checkpoint, "postTransitionMoved", MetricOperator.Eq);
        AssertMetric(step.Checkpoint, "transitionPending", MetricOperator.Eq);
    }

    [Fact]
    public async Task UnknownIdAndMissingConfirmationAreRejectedBeforeExecution()
    {
        var fake = new FakeExecutionService();
        await using var process = new GameProcessManager();
        await using var client = new GameAutomationClient();
        var tools = new SotnTools(process, client, new ScenarioCatalog(), fake,
            new ScenarioExecutionGate());

        await Assert.ThrowsAsync<McpException>(() =>
            tools.RunScenario("unknown", true, CancellationToken.None));
        await Assert.ThrowsAsync<McpException>(() => tools.RunScenario(
            ScenarioCatalog.CoopLocomotionJumpId, false, CancellationToken.None));

        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public async Task CatalogCommandReachesServiceAndReturnsArtifactId()
    {
        var fake = new FakeExecutionService();
        await using var process = new GameProcessManager();
        await using var client = new GameAutomationClient();
        var tools = new SotnTools(process, client, new ScenarioCatalog(), fake,
            new ScenarioExecutionGate());

        ScenarioExecutionResult result = await tools.RunScenario(
            ScenarioCatalog.CoopLocomotionJumpId, true, CancellationToken.None);

        Assert.Equal("artifact-test", result.ArtifactId);
        Assert.Equal(1, fake.Calls);
        Assert.Equal(ScenarioCatalog.CoopLocomotionJumpId,
            ScenarioParser.Parse(fake.Source!).Id);
    }

    [Fact]
    public async Task ConcurrentScenarioAndDirectMutationAreRejectedWithoutWaiting()
    {
        var fake = new FakeExecutionService { Block = true };
        var gate = new ScenarioExecutionGate();
        await using var process = new GameProcessManager();
        await using var client = new GameAutomationClient();
        var tools = new SotnTools(process, client, new ScenarioCatalog(), fake, gate);
        Task<ScenarioExecutionResult> first = tools.RunScenario(
            ScenarioCatalog.CoopLocomotionJumpId, true, CancellationToken.None);
        await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<McpException>(() => tools.RunScenario(
            ScenarioCatalog.CoopLocomotionJumpId, true, CancellationToken.None));
        await Assert.ThrowsAsync<McpException>(() => tools.RunInput(
            0, [new InputStep([], 1)], CancellationToken.None));
        await Assert.ThrowsAsync<McpException>(() => tools.ClearInput(CancellationToken.None));
        Assert.Equal(1, fake.Calls);

        fake.Release.SetResult();
        await first;
        using IDisposable mutation = gate.TryEnterMutation();
    }

    private static void AssertGame(ScenarioCheckpoint checkpoint, GamePredicateField field,
        string? text = null, bool? boolean = null, long? integer = null)
    {
        GameScenarioPredicate predicate = Assert.Single(
            checkpoint.Predicates.OfType<GameScenarioPredicate>(), value => value.Field == field);
        Assert.Equal(text, predicate.Expected.String);
        Assert.Equal(boolean, predicate.Expected.Boolean);
        Assert.Equal(integer, predicate.Expected.SignedInteger);
    }

    private static void AssertDiagnostic(ScenarioCheckpoint checkpoint, string field,
        string? exact = null, DiagnosticResult? result = null)
    {
        DiagnosticScenarioPredicate predicate = Assert.Single(
            checkpoint.Predicates.OfType<DiagnosticScenarioPredicate>(), value => value.Field == field);
        Assert.Equal("p2d4/2", predicate.Schema);
        Assert.Equal(exact, predicate.ExactValue);
        Assert.Equal(result, predicate.Result);
    }

    private static void AssertMetric(ScenarioCheckpoint checkpoint, string name, MetricOperator operation)
    {
        MetricScenarioPredicate predicate = Assert.Single(
            checkpoint.Predicates.OfType<MetricScenarioPredicate>(), value => value.Name == name);
        Assert.Equal("p2d4/2", predicate.Schema);
        Assert.Equal(operation, predicate.Operator);
    }

    private sealed class FakeExecutionService : IScenarioExecutionService
    {
        public int Calls { get; private set; }
        public string? Source { get; private set; }
        public bool Block { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ScenarioExecutionResult> RunAsync(string source,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Source = source;
            Entered.TrySetResult();
            if (Block) await Release.Task.WaitAsync(cancellationToken);
            return Result();
        }

        private static ScenarioExecutionResult Result()
        {
            var run = new ScenarioRunResult(ScenarioRunOutcome.Passed, null, [], 1, 65, [],
                null, null, null, true, true, true, null);
            var manifest = new ScenarioArtifactManifest(ScenarioExecutionService.ManifestSchema,
                "artifact-test", new ScenarioSourceIdentity(ScenarioParser.SchemaV2,
                    ScenarioCatalog.CoopLocomotionJumpId, ScenarioCatalog.CoopLocomotionJumpVersion,
                    new string('a', 64)),
                new ScenarioRuntimeIdentity("1", "test", AutomationProtocol.Version, null,
                    null, null, "running"), null, null, DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch, "Passed", null, null, 1, 65,
                new ScenarioCleanupManifest(true, true, true), ["state", "diagnostics"],
                ["state", "diagnostics"], []);
            return new ScenarioExecutionResult("artifact-test", "artifact-test", run, manifest);
        }
    }
}
