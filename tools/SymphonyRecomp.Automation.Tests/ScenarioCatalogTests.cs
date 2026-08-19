using ModelContextProtocol;
using SymphonyRecomp.Automation.Contracts;
using SymphonyRecomp.Mcp;
using SymphonyRecomp.Mcp.Scenarios;

namespace SymphonyRecomp.Automation.Tests;

public sealed class ScenarioCatalogTests
{
    [Fact]
    public void EmbeddedCanonicalSourceHasExactTimelinePredicatesAndPolicy()
    {
        var catalog = new ScenarioCatalog();

        ScenarioDefinition scenario = ScenarioParser.Parse(
            catalog.GetSource(ScenarioCatalog.CoopLocomotionJumpId));

        Assert.Equal("coop-locomotion-jump", scenario.Id);
        Assert.Equal("1", scenario.Version);
        Assert.Equal("coop-feasibility", scenario.ModId);
        Assert.Equal(128, scenario.TotalInputFrames);
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
        AssertDiagnostic(scenario.Start, "E", exact: "0");

        ScenarioStep step = Assert.Single(scenario.Steps);
        Assert.Equal("locomotion-jump", step.Id);
        ScenarioInput port0 = Assert.Single(step.Inputs, input => input.Port == 0);
        Assert.Equal(64, port0.TotalFrames);
        InputSegmentDto neutral = Assert.Single(port0.Timeline);
        Assert.Equal((ushort)0, neutral.Buttons);
        Assert.Equal(64, neutral.Frames);

        ScenarioInput port1 = Assert.Single(step.Inputs, input => input.Port == 1);
        Assert.Equal(64, port1.TotalFrames);
        Assert.Equal(new ushort[] { 0x2000, 0, 0x8000, 0, 0x0040, 0 },
            port1.Timeline.Select(segment => segment.Buttons));
        Assert.Equal(new[] { 12, 4, 12, 4, 1, 31 },
            port1.Timeline.Select(segment => segment.Frames));
        AssertDiagnostic(step.Checkpoint, "M", result: DiagnosticResult.Pass);
        AssertDiagnostic(step.Checkpoint, "E", exact: "0");
        Assert.DoesNotContain(step.Checkpoint.Predicates.OfType<DiagnosticScenarioPredicate>(),
            predicate => predicate.Field == "J");
        Assert.Equal(new[] { ScenarioArtifact.State, ScenarioArtifact.Diagnostics,
            ScenarioArtifact.Entities, ScenarioArtifact.Logs, ScenarioArtifact.Screenshot },
            scenario.Artifacts.OnFailure);
        Assert.Equal(new[] { ScenarioArtifact.State, ScenarioArtifact.Diagnostics },
            scenario.Artifacts.OnSuccess);
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
        string? text = null, bool? boolean = null)
    {
        GameScenarioPredicate predicate = Assert.Single(
            checkpoint.Predicates.OfType<GameScenarioPredicate>(), value => value.Field == field);
        Assert.Equal(text, predicate.Expected.String);
        Assert.Equal(boolean, predicate.Expected.Boolean);
    }

    private static void AssertDiagnostic(ScenarioCheckpoint checkpoint, string field,
        string? exact = null, DiagnosticResult? result = null)
    {
        DiagnosticScenarioPredicate predicate = Assert.Single(
            checkpoint.Predicates.OfType<DiagnosticScenarioPredicate>(), value => value.Field == field);
        Assert.Equal("p2d4/1", predicate.Schema);
        Assert.Equal(exact, predicate.ExactValue);
        Assert.Equal(result, predicate.Result);
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
                "artifact-test", new ScenarioSourceIdentity(ScenarioParser.Schema,
                    ScenarioCatalog.CoopLocomotionJumpId, "1", new string('a', 64)),
                new ScenarioRuntimeIdentity("1", "test", AutomationProtocol.Version, null,
                    null, null, "running"), null, null, DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch, "Passed", null, null, 1, 65,
                new ScenarioCleanupManifest(true, true, true), ["state", "diagnostics"],
                ["state", "diagnostics"], []);
            return new ScenarioExecutionResult("artifact-test", "artifact-test", run, manifest);
        }
    }
}
