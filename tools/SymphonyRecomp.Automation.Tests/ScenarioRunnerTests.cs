using System.Text.Json;
using SymphonyRecomp.Automation.Contracts;
using SymphonyRecomp.Mcp;
using SymphonyRecomp.Mcp.Scenarios;

namespace SymphonyRecomp.Automation.Tests;

public sealed class ScenarioRunnerTests
{
    private const string Session = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task SuccessPollsInOrderResetsExactlyRunsAscendingPortsAndWaitsForNeutral()
    {
        var fake = new ScriptedClient();
        fake.Telemetry.Enqueue(Telemetry(10, state: "Boot"));
        fake.Telemetry.Enqueue(Telemetry(11));
        fake.Telemetry.Enqueue(Telemetry(12));
        fake.Telemetry.Enqueue(Telemetry(13, active: true));
        fake.Telemetry.Enqueue(Telemetry(14));
        fake.Telemetry.Enqueue(Telemetry(15));
        fake.Diagnostics.Enqueue(Diagnostics(10, 4, "W:starting"));
        fake.Diagnostics.Enqueue(Diagnostics(11, 4, "P:ready"));
        fake.Diagnostics.Enqueue(Diagnostics(11, 5, "W:reset"));
        fake.Diagnostics.Enqueue(Diagnostics(15, 5, "P:complete"));
        ScenarioDefinition scenario = Scenario(
            start: new ScenarioCheckpoint(20,
            [
                Game(GamePredicateField.State, text: "Play"),
                Game(GamePredicateField.Stage, text: "CEN"),
                Game(GamePredicateField.Character, text: "Alucard"),
                Game(GamePredicateField.GameStepRaw, number: 2),
                Game(GamePredicateField.EngineStepRaw, number: 3),
                Game(GamePredicateField.Loading, boolean: false),
                Game(GamePredicateField.MenuOpen, boolean: false),
                Game(GamePredicateField.MapOpen, boolean: false),
                Game(GamePredicateField.PlayerHasControl, boolean: true),
                Diagnostic(DiagnosticResult.Pass),
            ]),
            steps:
            [
                Step("both",
                    [Input(1, 2), Input(0, 3)],
                    new ScenarioCheckpoint(20, [Diagnostic(DiagnosticResult.Pass), Exact("VER", "0.4.0")]))
            ]);

        ScenarioRunResult result = await new ScenarioRunner(fake, new FakeClock()).RunAsync(scenario);

        Assert.Equal(ScenarioRunOutcome.Passed, result.Outcome);
        Assert.Equal(["status", "telemetry", "diagnostics", "telemetry", "diagnostics", "reset", "diagnostics"],
            fake.Events.Take(7));
        Assert.Equal(new ModDiagnosticsResetRequest("coop", Session, 4, true), fake.ResetRequest);
        Assert.Equal(new ScenarioDiagnosticIdentity(Session, 4, 11, "p2d4/1"), result.InitialDiagnostics);
        Assert.Equal(new ScenarioDiagnosticIdentity(Session, 5, 11, "p2d4/1"), result.PostResetDiagnostics);
        Assert.Equal([0, 1], fake.InputRequests.Select(value => value.Port));
        Assert.Equal([13L, 13L], result.InputOperations.Select(value => value.StartsAfterFrame));
        Assert.True(result.CleanupSucceeded);
        Assert.True(result.CleanupVerified);
        Assert.Equal(1, fake.ClearCalls);
    }

    [Fact]
    public async Task TerminalDiagnosticFailureStopsImmediately()
    {
        var fake = new ScriptedClient();
        fake.Diagnostics.Enqueue(Diagnostics(1, 0, "F:broken"));

        ScenarioRunResult result = await Run(fake, Scenario(start: Checkpoint(DiagnosticResult.Pass)));

        Assert.Equal(ScenarioRunOutcome.Failed, result.Outcome);
        Assert.Equal("start", result.FailedCheckpoint);
        Assert.Contains("F:broken", result.UnmetPredicates.Single().Observed);
        Assert.DoesNotContain("delay", fake.Events);
        Assert.Empty(fake.InputRequests);
    }

    [Fact]
    public async Task FrameDeadlineUsesObservedFrames()
    {
        var fake = new ScriptedClient();
        fake.Telemetry.Enqueue(Telemetry(20, state: "Boot"));
        fake.Telemetry.Enqueue(Telemetry(25, state: "Boot"));
        fake.Diagnostics.Enqueue(Diagnostics(20, 0));
        fake.Diagnostics.Enqueue(Diagnostics(25, 0));

        ScenarioRunResult result = await Run(fake, Scenario(start: new ScenarioCheckpoint(5,
            [Game(GamePredicateField.State, text: "Play")])));

        Assert.Equal(ScenarioRunOutcome.Failed, result.Outcome);
        Assert.Contains("frame deadline", result.PrimaryError);
        Assert.Equal(25, result.FinalFrame);
    }

    [Fact]
    public async Task LaterDiagnosticFrameCannotSatisfyPredicateAfterDeadline()
    {
        var fake = new ScriptedClient();
        fake.Telemetry.Enqueue(Telemetry(20));
        fake.Diagnostics.Enqueue(Diagnostics(26, 0));

        ScenarioRunResult result = await Run(fake, Scenario(start: new ScenarioCheckpoint(5,
            [Game(GamePredicateField.State, text: "Play")]), steps: []));

        Assert.Equal(ScenarioRunOutcome.Failed, result.Outcome);
        Assert.Contains("frame deadline", result.PrimaryError);
        Assert.Equal(26, result.FinalFrame);
    }

    [Fact]
    public async Task DiagnosticFrameEarlierThanPrecedingTelemetryFailsClosed()
    {
        var fake = new ScriptedClient();
        fake.Telemetry.Enqueue(Telemetry(20));
        fake.Diagnostics.Enqueue(Diagnostics(19, 0));

        ScenarioRunResult result = await Run(fake, Scenario(steps: []));

        Assert.Equal(ScenarioRunOutcome.Failed, result.Outcome);
        Assert.Contains("diagnostic frame", result.PrimaryError);
    }

    [Fact]
    public async Task NoFrameProgressTerminatesWithoutUnboundedPolling()
    {
        var fake = new ScriptedClient { FallbackTelemetry = Telemetry(7, state: "Boot") };
        fake.FallbackDiagnostics = Diagnostics(7, 0);
        var clock = new FakeClock();

        ScenarioRunResult result = await new ScenarioRunner(fake, clock).RunAsync(
            Scenario(start: new ScenarioCheckpoint(100, [Game(GamePredicateField.State, text: "Play")])));

        Assert.Equal(ScenarioRunOutcome.Failed, result.Outcome);
        Assert.Contains("no automation frame progress", result.PrimaryError);
        Assert.Equal(32, clock.Delays);
    }

    [Fact]
    public async Task FirstFailedStepSuppressesLaterInput()
    {
        var fake = SuccessfulStart();
        fake.Telemetry.Enqueue(Telemetry(2));
        fake.Telemetry.Enqueue(Telemetry(3));
        fake.Telemetry.Enqueue(Telemetry(5, state: "Boot"));
        fake.Diagnostics.Enqueue(Diagnostics(5, 1));
        ScenarioDefinition scenario = Scenario(steps:
        [
            Step("first", [Input(0, 1)], new ScenarioCheckpoint(2,
                [Game(GamePredicateField.State, text: "Never")])),
            Step("second", [Input(1, 1)], Checkpoint()),
        ]);

        ScenarioRunResult result = await Run(fake, scenario);

        Assert.Equal(ScenarioRunOutcome.Failed, result.Outcome);
        Assert.Equal("first", result.FailedCheckpoint);
        Assert.Single(fake.InputRequests);
        Assert.Equal(0, fake.InputRequests[0].Port);
    }

    [Fact]
    public async Task CancellationStillUsesIndependentCleanupToken()
    {
        using var cancellation = new CancellationTokenSource();
        var fake = new ScriptedClient { FallbackTelemetry = Telemetry(1, state: "Boot") };
        fake.FallbackDiagnostics = Diagnostics(1, 0);
        var clock = new FakeClock(() => cancellation.Cancel());

        ScenarioRunResult result = await new ScenarioRunner(fake, clock).RunAsync(
            Scenario(start: new ScenarioCheckpoint(100, [Game(GamePredicateField.State, text: "Play")])),
            cancellation.Token);

        Assert.Equal(ScenarioRunOutcome.Cancelled, result.Outcome);
        Assert.Equal(1, fake.ClearCalls);
        Assert.True(fake.ClearToken.CanBeCanceled);
        Assert.False(fake.ClearToken.IsCancellationRequested);
        Assert.True(result.CleanupVerified);
    }

    [Fact]
    public async Task InterruptionAfterInputIsIndeterminateAndInputIsNotRetried()
    {
        var fake = SuccessfulStart();
        fake.Telemetry.Enqueue(Telemetry(2));
        fake.ThrowOnInput = new IOException("secret /path token=abc");

        ScenarioRunResult result = await Run(fake, Scenario(steps: [Step("one", [Input(0, 1)], Checkpoint())]));

        Assert.Equal(ScenarioRunOutcome.Indeterminate, result.Outcome);
        Assert.Equal(1, fake.InputAttempts);
        Assert.DoesNotContain("secret", result.PrimaryError);
        Assert.Equal(1, fake.ClearCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupRunsForFailureAndException(bool exception)
    {
        var fake = new ScriptedClient();
        if (exception) fake.ThrowOnStatus = new InvalidOperationException("boom");
        else fake.Diagnostics.Enqueue(Diagnostics(1, 0, "F"));

        ScenarioRunResult result = await Run(fake, Scenario());

        Assert.Equal(ScenarioRunOutcome.Failed, result.Outcome);
        Assert.Equal(1, fake.ClearCalls);
        Assert.True(result.CleanupAttempted);
    }

    [Fact]
    public async Task CleanupFailureChangesSuccessToCleanupFailed()
    {
        var fake = SuccessfulStart();
        fake.ThrowOnClear = new IOException("unreachable");

        ScenarioRunResult result = await Run(fake, Scenario(steps: []));

        Assert.Equal(ScenarioRunOutcome.CleanupFailed, result.Outcome);
        Assert.False(result.CleanupSucceeded);
        Assert.False(result.CleanupVerified);
        Assert.Equal(1, fake.ClearCalls);
    }

    [Fact]
    public async Task CleanupFailureDoesNotErasePrimaryFailure()
    {
        var fake = new ScriptedClient { ThrowOnClear = new IOException("cleanup") };
        fake.Diagnostics.Enqueue(Diagnostics(1, 0, "F:primary"));

        ScenarioRunResult result = await Run(fake, Scenario());

        Assert.Equal(ScenarioRunOutcome.Failed, result.Outcome);
        Assert.Contains("terminal diagnostic failure", result.PrimaryError);
        Assert.False(result.CleanupSucceeded);
    }

    [Fact]
    public async Task MalformedDiagnosticEnvelopeFailsClosedBeforeResetOrInput()
    {
        var fake = new ScriptedClient();
        fake.Diagnostics.Enqueue(new ModDiagnosticsDto("coop", 1, Session, 0,
            JsonSerializer.SerializeToElement(new { schema = "p2d4/1", sessionId = Session,
                generation = 99, automationFrame = 1, fields = new { M = "P" } })));

        ScenarioRunResult result = await Run(fake, Scenario());

        Assert.Equal(ScenarioRunOutcome.Failed, result.Outcome);
        Assert.Equal("start", result.FailedCheckpoint);
        Assert.Contains("envelope", result.UnmetPredicates.Single().Predicate);
        Assert.Null(fake.ResetRequest);
        Assert.Empty(fake.InputRequests);
    }

    [Fact]
    public async Task MissingAndMalformedDiagnosticFieldsFailClosed()
    {
        foreach (ModDiagnosticsDto diagnostics in new[]
        {
            Diagnostics(1, 0, fields: new Dictionary<string, string> { ["VER"] = "0.4.0" }),
            Diagnostics(1, 0, "?:unknown"),
        })
        {
            var fake = new ScriptedClient();
            fake.Diagnostics.Enqueue(diagnostics);
            ScenarioRunResult result = await Run(fake, Scenario());
            Assert.Equal(ScenarioRunOutcome.Failed, result.Outcome);
            Assert.Single(fake.DiagnosticsSeen);
        }
    }

    [Fact]
    public async Task BridgeMustBeReadyOnExactProtocol()
    {
        var fake = new ScriptedClient
        {
            Status = new BridgeStatusDto("1.1", true, 9, 1, "Running", "Play", "CEN", 0, false),
        };

        ScenarioRunResult result = await Run(fake, Scenario());

        Assert.Equal(ScenarioRunOutcome.Failed, result.Outcome);
        Assert.Empty(fake.DiagnosticsSeen);
        Assert.Equal(1, fake.ClearCalls);
    }

    [Fact]
    public async Task V2NonePreservesIdentityAndUsesOneAtomicDualPortBatch()
    {
        ScenarioDefinition scenario = ScenarioParser.Parse("""
            {"schema":"sotn-scenario/2","id":"v2-run","version":"1","description":"v2 runner","modId":"coop","timeoutMs":30000,"diagnosticsReset":"none","start":{"timeoutFrames":20,"predicates":[{"type":"metric","schema":"p2d4/2","name":"errorCode","operator":"eq","value":"0"}]},"steps":[{"id":"dual","inputs":[{"port":0,"timeline":[{"buttons":["Down"],"frames":1}]},{"port":1,"timeline":[{"buttons":["Circle"],"frames":1}]}],"checkpoint":{"timeoutFrames":20,"predicates":[{"type":"metric","schema":"p2d4/2","name":"healthHp","operator":"gte","value":50}]}}],"artifacts":{"onFailure":[],"onSuccess":[]}}
            """);
        var fake = new ScriptedClient();
        foreach (long frame in new long[] { 1, 2, 3, 4 }) fake.Telemetry.Enqueue(Telemetry(frame));
        fake.Diagnostics.Enqueue(DiagnosticsV2(1, 7));
        fake.Diagnostics.Enqueue(DiagnosticsV2(4, 7));
        fake.FallbackTelemetry = Telemetry(5);
        fake.FallbackDiagnostics = DiagnosticsV2(5, 7);

        ScenarioRunResult result = await Run(fake, scenario);

        Assert.Equal(ScenarioRunOutcome.Passed, result.Outcome);
        Assert.Null(fake.ResetRequest);
        Assert.Contains("input:batch", fake.Events);
        Assert.Equal(2, result.InputOperations.Count);
        Assert.Single(result.InputOperations.Select(value => value.StartsAfterFrame).Distinct());
        Assert.Equal(7, result.InitialDiagnostics?.Generation);
        Assert.Equal(7, result.FinalDiagnostics?.Generation);
    }

    [Fact]
    public async Task V2MissingMetricFailsClosedAtEnvelope()
    {
        ScenarioDefinition scenario = ScenarioParser.Parse("""
            {"schema":"sotn-scenario/2","id":"v2-bad","version":"1","description":"v2 bad","modId":"coop","timeoutMs":30000,"diagnosticsReset":"none","start":{"timeoutFrames":20,"predicates":[{"type":"metric","schema":"p2d4/2","name":"fatal","operator":"eq","value":false}]},"steps":[{"id":"one","inputs":[{"port":0,"timeline":[{"buttons":[],"frames":1}]}],"checkpoint":{"timeoutFrames":20,"predicates":[{"type":"metric","schema":"p2d4/2","name":"fatal","operator":"eq","value":false}]}}],"artifacts":{"onFailure":[],"onSuccess":[]}}
            """);
        var fake = new ScriptedClient();
        fake.Telemetry.Enqueue(Telemetry(1));
        fake.Diagnostics.Enqueue(DiagnosticsV2(1, 0, "fatal"));

        ScenarioRunResult result = await Run(fake, scenario);

        Assert.Equal(ScenarioRunOutcome.Failed, result.Outcome);
        Assert.Equal("start", result.FailedCheckpoint);
        Assert.Contains(result.UnmetPredicates, value => value.Predicate == "diagnostics.envelope");
    }

    [Theory]
    [InlineData("deltaEq", -3, -3)]
    [InlineData("deltaGte", 4, 3)]
    [InlineData("deltaLte", -4, -3)]
    public async Task V2DeltaOperatorsCompareSignedChangeFromResetBaseline(
        string operation, long change, long expected)
    {
        ScenarioDefinition scenario = DeltaScenario(operation, expected, DiagnosticsResetPolicy.Before);
        var fake = new ScriptedClient();
        fake.Telemetry.Enqueue(Telemetry(1));
        fake.Telemetry.Enqueue(Telemetry(2));
        fake.Telemetry.Enqueue(Telemetry(3));
        fake.Telemetry.Enqueue(Telemetry(4));
        fake.Diagnostics.Enqueue(DiagnosticsV2(1, 2, values: new() { ["attackAllocations"] = 99 }));
        fake.Diagnostics.Enqueue(DiagnosticsV2(1, 3, values: new() { ["attackAllocations"] = 10 }));
        fake.Diagnostics.Enqueue(DiagnosticsV2(4, 3, values: new() { ["attackAllocations"] = 10 + change }));

        Assert.Equal(ScenarioRunOutcome.Passed, (await Run(fake, scenario)).Outcome);
    }

    [Fact]
    public async Task V2ResetNoneUsesAcceptedStartAndKeepsBaselineAcrossSteps()
    {
        ScenarioDefinition scenario = DeltaScenario("deltaEq", 5, DiagnosticsResetPolicy.None, twoSteps: true);
        var fake = new ScriptedClient();
        foreach (long frame in new long[] { 1, 2, 3, 4, 5, 6, 7 }) fake.Telemetry.Enqueue(Telemetry(frame));
        fake.Diagnostics.Enqueue(DiagnosticsV2(1, 7, values: new() { ["attackAllocations"] = 20 }));
        fake.Diagnostics.Enqueue(DiagnosticsV2(4, 7, values: new() { ["attackAllocations"] = 22 }));
        fake.Diagnostics.Enqueue(DiagnosticsV2(7, 7, values: new() { ["attackAllocations"] = 25 }));

        Assert.Equal(ScenarioRunOutcome.Passed, (await Run(fake, scenario)).Outcome);
    }

    [Fact]
    public async Task V2DeltaFailsClosedOnIdentityChangeAndArithmeticOverflow()
    {
        foreach ((long start, long current, int finalGeneration) in new[]
        {
            (0L, 1L, 8),
            (long.MinValue, long.MaxValue, 7),
        })
        {
            ScenarioDefinition scenario = DeltaScenario("deltaGte", 0, DiagnosticsResetPolicy.None);
            var fake = new ScriptedClient();
            foreach (long frame in new long[] { 1, 2, 3, 4 }) fake.Telemetry.Enqueue(Telemetry(frame));
            fake.Diagnostics.Enqueue(DiagnosticsV2(1, 7, values: new() { ["attackAllocations"] = start }));
            fake.Diagnostics.Enqueue(DiagnosticsV2(4, finalGeneration,
                values: new() { ["attackAllocations"] = current }));

            ScenarioRunResult result = await Run(fake, scenario);
            Assert.Equal(ScenarioRunOutcome.Failed, result.Outcome);
            Assert.Contains(finalGeneration == 8 ? "identity" : "terminal", result.PrimaryError!);
        }
    }

    private static Task<ScenarioRunResult> Run(ScriptedClient fake, ScenarioDefinition scenario) =>
        new ScenarioRunner(fake, new FakeClock()).RunAsync(scenario);

    private static ScriptedClient SuccessfulStart()
    {
        var fake = new ScriptedClient();
        fake.Telemetry.Enqueue(Telemetry(1));
        fake.Diagnostics.Enqueue(Diagnostics(1, 0));
        fake.Diagnostics.Enqueue(Diagnostics(1, 1));
        return fake;
    }

    private static ScenarioDefinition Scenario(ScenarioCheckpoint? start = null,
        IReadOnlyList<ScenarioStep>? steps = null) => new(
        ScenarioParser.Schema, "test", "1", "test", "coop", 30000,
        DiagnosticsResetPolicy.Before,
        start ?? Checkpoint(), steps ?? [Step("step", [Input(0, 1)], Checkpoint())],
        new ScenarioArtifactPolicy([], []), 1);

    private static ScenarioDefinition DeltaScenario(string operation, long expected,
        DiagnosticsResetPolicy reset, bool twoSteps = false)
    {
        MetricOperator op = operation switch
        {
            "deltaEq" => MetricOperator.DeltaEq,
            "deltaGte" => MetricOperator.DeltaGte,
            "deltaLte" => MetricOperator.DeltaLte,
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        ScenarioCheckpoint Metric(long value) => new(20,
            [new MetricScenarioPredicate("p2d4/2", "attackAllocations", op,
                MetricScalarType.Integer, new ScenarioScalar(null, null, null, value))]);
        var steps = new List<ScenarioStep> { Step("one", [Input(0, 1)], Metric(twoSteps ? 2 : expected)) };
        if (twoSteps) steps.Add(Step("two", [Input(0, 1)], Metric(expected)));
        return new(ScenarioParser.SchemaV2, "delta", "1", "delta test", "coop", 30000, reset,
            new ScenarioCheckpoint(20, [new MetricScenarioPredicate("p2d4/2", "fatal",
                MetricOperator.Eq, MetricScalarType.Boolean,
                new ScenarioScalar(null, null, false, null))]), steps,
            new ScenarioArtifactPolicy([], []), steps.Count);
    }

    private static ScenarioCheckpoint Checkpoint(DiagnosticResult result = DiagnosticResult.Pass) =>
        new(20, [Diagnostic(result)]);

    private static ScenarioStep Step(string id, IReadOnlyList<ScenarioInput> inputs,
        ScenarioCheckpoint checkpoint) => new(id, inputs, checkpoint);

    private static ScenarioInput Input(int port, int frames) =>
        new(port, [new InputSegmentDto(0x2000, frames)], frames);

    private static DiagnosticScenarioPredicate Diagnostic(DiagnosticResult result) =>
        new("p2d4/1", "M", null, result);

    private static DiagnosticScenarioPredicate Exact(string field, string value) =>
        new("p2d4/1", field, value, null);

    private static GameScenarioPredicate Game(GamePredicateField field, string? text = null,
        uint? number = null, bool? boolean = null) => new(field, new ScenarioScalar(text, number, boolean));

    private static CombinedTelemetryDto Telemetry(long frame, string state = "Play", bool active = false)
    {
        var runtime = new RuntimeTelemetryDto(frame, 1, frame * 16, 0, [], 0, null, null, null);
        var player = new PlayerTelemetryDto(0, 0, 0, 0, 0, 0, false, "Stand", 0, true, false,
            10, 10, 10, 10, 0, 0, 1, 0, 0, 0);
        var input = new InputTelemetryDto(0, 0, 0, 0, active ? (ushort)1 : (ushort)0, 0,
            active ? 1 : 0, 0);
        var game = new GameTelemetryDto(true, state, "Play", 2, 3, "Alucard", "CEN", 0, 0, 0, 0,
            false, false, false, 0, 0, player, input, null, null);
        return new CombinedTelemetryDto(runtime, game);
    }

    private static ModDiagnosticsDto Diagnostics(long frame, int generation, string result = "P",
        IReadOnlyDictionary<string, string>? fields = null)
    {
        fields ??= new Dictionary<string, string> { ["M"] = result, ["VER"] = "0.4.0" };
        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            schema = "p2d4/1",
            sessionId = Session,
            generation,
            automationFrame = frame,
            fields,
        });
        return new ModDiagnosticsDto("coop", frame, Session, generation, payload);
    }

    private static ModDiagnosticsDto DiagnosticsV2(long frame, int generation, string? omitMetric = null,
        Dictionary<string, long>? values = null)
    {
        var metrics = ScenarioMetricContract.Types.ToDictionary(pair => pair.Key, pair => pair.Value switch
        {
            MetricScalarType.Integer => (object)(values != null && values.TryGetValue(pair.Key, out long value)
                ? value : pair.Key == "healthHp" ? 100L : 0L),
            MetricScalarType.Boolean => false,
            MetricScalarType.String => "0",
            _ => throw new InvalidOperationException(),
        }, StringComparer.Ordinal);
        if (omitMetric is not null) metrics.Remove(omitMetric);
        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            schema = "p2d4/2", modVersion = "0.4.0", sessionId = Session, generation,
            modFrame = frame, automationFrame = frame, legacy = "P2D4 test",
            fields = P2D4Fields(), metrics,
        });
        return new ModDiagnosticsDto("coop", frame, Session, generation, payload);
    }

    private static Dictionary<string, string> P2D4Fields(IReadOnlyDictionary<string, string>? overrides = null)
    {
        string[] names = ["VER", "H", "I", "K", "M", "R", "N", "B", "C", "T", "S", "G", "Q", "A", "E",
            "D", "VIS", "J", "X", "EN", "AW", "HU", "HP"];
        var result = names.ToDictionary(name => name, name => name == "VER" ? "0.4.0" : "P",
            StringComparer.Ordinal);
        if (overrides is not null)
            foreach ((string key, string value) in overrides) result[key] = value;
        return result;
    }

    private sealed class FakeClock(Action? onDelay = null) : IScenarioClock
    {
        public int Delays { get; private set; }

        public Task DelayAsync(TimeSpan delay, CancellationToken token)
        {
            Delays++;
            onDelay?.Invoke();
            return token.IsCancellationRequested ? Task.FromCanceled(token) : Task.CompletedTask;
        }
    }

    private sealed class ScriptedClient : IScenarioAutomationClient
    {
        public Queue<CombinedTelemetryDto> Telemetry { get; } = new();
        public Queue<ModDiagnosticsDto> Diagnostics { get; } = new();
        public List<string> Events { get; } = new();
        public List<InputTimelineRequest> InputRequests { get; } = new();
        public List<ModDiagnosticsDto> DiagnosticsSeen { get; } = new();
        public BridgeStatusDto Status { get; set; } =
            new(AutomationProtocol.Version, true, 0, 1, "Running", "Play", "CEN", 0, false);
        public CombinedTelemetryDto FallbackTelemetry { get; set; } = ScenarioRunnerTests.Telemetry(1);
        public ModDiagnosticsDto FallbackDiagnostics { get; set; } = ScenarioRunnerTests.Diagnostics(1, 1);
        public ModDiagnosticsResetRequest? ResetRequest { get; private set; }
        public Exception? ThrowOnStatus { get; set; }
        public Exception? ThrowOnInput { get; set; }
        public Exception? ThrowOnClear { get; set; }
        public int InputAttempts { get; private set; }
        public int ClearCalls { get; private set; }
        public CancellationToken ClearToken { get; private set; }
        private bool _cleared;

        public Task<BridgeStatusDto> GetBridgeStatusAsync(CancellationToken token)
        {
            Events.Add("status");
            if (ThrowOnStatus is not null) return Task.FromException<BridgeStatusDto>(ThrowOnStatus);
            return Task.FromResult(Status);
        }

        public Task<CombinedTelemetryDto> GetTelemetryAsync(CancellationToken token)
        {
            Events.Add("telemetry");
            CombinedTelemetryDto value = Telemetry.Count != 0 ? Telemetry.Dequeue() : FallbackTelemetry;
            if (_cleared) value = ScenarioRunnerTests.Telemetry(value.Runtime.Frame);
            return Task.FromResult(value);
        }

        public Task<ModDiagnosticsDto> CaptureModDiagnosticsAsync(ModDiagnosticsCaptureRequest request,
            CancellationToken token)
        {
            Events.Add("diagnostics");
            ModDiagnosticsDto value = Diagnostics.Count != 0 ? Diagnostics.Dequeue() : FallbackDiagnostics;
            DiagnosticsSeen.Add(value);
            return Task.FromResult(value);
        }

        public Task<ModDiagnosticsResetDto> ResetModDiagnosticsAsync(ModDiagnosticsResetRequest request,
            CancellationToken token)
        {
            Events.Add("reset");
            ResetRequest = request;
            return Task.FromResult(new ModDiagnosticsResetDto(request.Id, FallbackTelemetry.Runtime.Frame, true));
        }

        public Task<InputOperationDto> RunInputAsync(InputTimelineRequest request, CancellationToken token)
        {
            Events.Add($"input:{request.Port}");
            InputAttempts++;
            if (ThrowOnInput is not null) return Task.FromException<InputOperationDto>(ThrowOnInput);
            InputRequests.Add(request);
            return Task.FromResult(new InputOperationDto(request.Port, request.Segments.Sum(value => value.Frames), 13));
        }

        public Task<InputBatchOperationDto> RunInputBatchAsync(InputBatchRequest request, CancellationToken token)
        {
            Events.Add("input:batch");
            InputAttempts++;
            if (ThrowOnInput is not null) return Task.FromException<InputBatchOperationDto>(ThrowOnInput);
            foreach (InputTimelineRequest timeline in request.Timelines) InputRequests.Add(timeline);
            InputOperationDto[] operations = request.Timelines.Select(timeline => new InputOperationDto(
                timeline.Port, timeline.Segments.Sum(value => value.Frames), 13)).ToArray();
            return Task.FromResult(new InputBatchOperationDto(13, operations));
        }

        public Task<OperationResultDto> ClearInputAsync(CancellationToken token)
        {
            Events.Add("clear");
            ClearCalls++;
            ClearToken = token;
            if (ThrowOnClear is not null) return Task.FromException<OperationResultDto>(ThrowOnClear);
            _cleared = true;
            return Task.FromResult(new OperationResultDto(true, FallbackTelemetry.Runtime.Frame, "cleared"));
        }

        public Task<ModTelemetryDto[]> ListModsAsync(CancellationToken token) =>
            Task.FromResult(Array.Empty<ModTelemetryDto>());

        public Task<EntityListDto> ListEntitiesAsync(int maximum, CancellationToken token) =>
            Task.FromResult(new EntityListDto(0, 0, []));

        public Task<LogsResult> GetLogsAsync(int maximum, CancellationToken token) =>
            Task.FromResult(new LogsResult(0, [], []));

        public Task<ScreenshotDto> CaptureScreenshotAsync(CancellationToken token) =>
            Task.FromException<ScreenshotDto>(new InvalidOperationException());

        public Task<ScenarioBuildIdentity> GetBuildIdentityAsync(CancellationToken token) =>
            Task.FromResult(new ScenarioBuildIdentity(null, null, "test", AutomationProtocol.Version,
                1, "running"));
    }
}
