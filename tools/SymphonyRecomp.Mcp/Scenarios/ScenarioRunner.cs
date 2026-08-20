using System.Text.Json;
using SymphonyRecomp.Automation.Contracts;

namespace SymphonyRecomp.Mcp.Scenarios;

public interface IScenarioAutomationClient
{
    Task<BridgeStatusDto> GetBridgeStatusAsync(CancellationToken token);
    Task<CombinedTelemetryDto> GetTelemetryAsync(CancellationToken token);
    Task<ModDiagnosticsDto> CaptureModDiagnosticsAsync(ModDiagnosticsCaptureRequest request,
        CancellationToken token);
    Task<ModDiagnosticsResetDto> ResetModDiagnosticsAsync(ModDiagnosticsResetRequest request,
        CancellationToken token);
    Task<InputOperationDto> RunInputAsync(InputTimelineRequest request, CancellationToken token);
    Task<InputBatchOperationDto> RunInputBatchAsync(InputBatchRequest request, CancellationToken token);
    Task<OperationResultDto> ClearInputAsync(CancellationToken token);
    Task<ModTelemetryDto[]> ListModsAsync(CancellationToken token);
    Task<EntityListDto> ListEntitiesAsync(int maximum, CancellationToken token);
    Task<LogsResult> GetLogsAsync(int maximum, CancellationToken token);
    Task<ScreenshotDto> CaptureScreenshotAsync(CancellationToken token);
    Task<ScenarioBuildIdentity> GetBuildIdentityAsync(CancellationToken token);
}

public interface IScenarioClock
{
    Task DelayAsync(TimeSpan delay, CancellationToken token);
}

public sealed class SystemScenarioClock : IScenarioClock
{
    public Task DelayAsync(TimeSpan delay, CancellationToken token) => Task.Delay(delay, token);
}

public enum ScenarioRunOutcome { Passed, Failed, Cancelled, Indeterminate, CleanupFailed }

public sealed record ScenarioDiagnosticIdentity(string SessionId, int Generation, long Frame, string? Schema = null);
public sealed record ScenarioInputOperation(string StepId, int Port, long StartsAfterFrame);
public sealed record ScenarioUnmetPredicate(int Index, string Predicate, string Observed);

public sealed record ScenarioRunResult(
    ScenarioRunOutcome Outcome,
    string? FailedCheckpoint,
    IReadOnlyList<ScenarioUnmetPredicate> UnmetPredicates,
    long? InitialFrame,
    long? FinalFrame,
    IReadOnlyList<ScenarioInputOperation> InputOperations,
    ScenarioDiagnosticIdentity? InitialDiagnostics,
    ScenarioDiagnosticIdentity? PostResetDiagnostics,
    ScenarioDiagnosticIdentity? FinalDiagnostics,
    bool CleanupAttempted,
    bool CleanupSucceeded,
    bool CleanupVerified,
    string? PrimaryError);

public sealed class ScenarioRunner
{
    private const int MaximumStagnantPolls = 32;
    private static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(2);

    private readonly IScenarioAutomationClient _client;
    private readonly IScenarioClock _clock;

    public ScenarioRunner(IScenarioAutomationClient client, IScenarioClock clock)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<ScenarioRunResult> RunAsync(ScenarioDefinition scenario,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        long? initialFrame = null;
        long? finalFrame = null;
        ScenarioDiagnosticIdentity? initialDiagnostics = null;
        ScenarioDiagnosticIdentity? postResetDiagnostics = null;
        ScenarioDiagnosticIdentity? finalDiagnostics = null;
        Envelope? metricBaseline = null;
        var operations = new List<ScenarioInputOperation>(scenario.Steps.Count * 2);
        IReadOnlyList<ScenarioUnmetPredicate> unmet = [];
        string? failedCheckpoint = null;
        string? primaryError = null;
        ScenarioRunOutcome outcome = ScenarioRunOutcome.Failed;
        bool inputSubmitted = false;
        bool cleanupSucceeded = false;
        bool cleanupVerified = false;

        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        watchdog.CancelAfter(TimeSpan.FromMilliseconds(scenario.TimeoutMs));
        try
        {
            BridgeStatusDto bridge = await _client.GetBridgeStatusAsync(watchdog.Token).ConfigureAwait(false);
            initialFrame = finalFrame = bridge.Frame;
            if (!bridge.Ready || bridge.ProtocolVersion != AutomationProtocol.Version)
            {
                primaryError = $"The automation bridge is not ready with protocol {AutomationProtocol.Version}.";
                outcome = ScenarioRunOutcome.Failed;
                goto Complete;
            }

            CheckpointResult start = await WaitForCheckpointAsync(scenario, "start", scenario.Start,
                watchdog.Token).ConfigureAwait(false);
            finalFrame = start.Frame;
            finalDiagnostics = start.Diagnostics;
            initialDiagnostics = start.Diagnostics;
            if (!start.Passed)
            {
                failedCheckpoint = "start";
                unmet = start.Unmet;
                primaryError = start.Error;
                outcome = ScenarioRunOutcome.Failed;
                goto Complete;
            }

            ScenarioDiagnosticIdentity identity = start.Diagnostics!;
            if (scenario.DiagnosticsReset == DiagnosticsResetPolicy.Before)
            {
                ModDiagnosticsResetDto reset = await _client.ResetModDiagnosticsAsync(
                    new ModDiagnosticsResetRequest(scenario.ModId, identity.SessionId, identity.Generation, true),
                    watchdog.Token).ConfigureAwait(false);
                if (!reset.Applied || reset.Id != scenario.ModId)
                {
                    primaryError = "The diagnostic reset was not applied.";
                    outcome = ScenarioRunOutcome.Failed;
                    goto Complete;
                }

                ModDiagnosticsDto resetCapture = await _client.CaptureModDiagnosticsAsync(
                    new ModDiagnosticsCaptureRequest(scenario.ModId), watchdog.Token).ConfigureAwait(false);
                Envelope resetEnvelope = ValidateEnvelope(resetCapture, scenario.ModId);
                postResetDiagnostics = finalDiagnostics = resetEnvelope.Identity;
                metricBaseline = resetEnvelope;
                finalFrame = Math.Max(finalFrame.Value, resetCapture.Frame);
                if (resetEnvelope.Identity.SessionId != identity.SessionId ||
                    resetEnvelope.Identity.Generation != identity.Generation + 1)
                {
                    primaryError = "The post-reset diagnostic identity was not the exact next generation.";
                    outcome = ScenarioRunOutcome.Failed;
                    goto Complete;
                }
            }
            else
            {
                postResetDiagnostics = finalDiagnostics = identity;
                metricBaseline = start.Envelope;
            }

            foreach (ScenarioStep step in scenario.Steps)
            {
                CombinedTelemetryDto beforeInput = await _client.GetTelemetryAsync(watchdog.Token)
                    .ConfigureAwait(false);
                finalFrame = beforeInput.Runtime.Frame;
                long deadlineStart = beforeInput.Runtime.Frame;
                if (scenario.Schema == ScenarioParser.SchemaV2 && step.Inputs.Count > 1)
                {
                    inputSubmitted = true;
                    InputBatchRequest request = new(step.Inputs.OrderBy(value => value.Port)
                        .Select(input => new InputTimelineRequest(input.Port, input.Timeline.ToArray())).ToArray());
                    InputBatchOperationDto batch = await _client.RunInputBatchAsync(request, watchdog.Token)
                        .ConfigureAwait(false);
                    if (batch.StartsAfterFrame < 0 || batch.Operations.Length != request.Timelines.Length ||
                        batch.Operations.Any(operation => operation.StartsAfterFrame != batch.StartsAfterFrame))
                        throw new InvalidDataException("The atomic input batch response was inconsistent.");
                    foreach (ScenarioInput input in step.Inputs.OrderBy(value => value.Port))
                    {
                        InputOperationDto? operation = batch.Operations.SingleOrDefault(value => value.Port == input.Port);
                        if (operation is null)
                            throw new InvalidDataException("The atomic input batch omitted a requested port.");
                        if (operation.TotalFrames != input.TotalFrames)
                            throw new InvalidDataException("The atomic input batch duration was inconsistent.");
                        operations.Add(new ScenarioInputOperation(step.Id, operation.Port, operation.StartsAfterFrame));
                    }
                }
                else
                {
                    foreach (ScenarioInput input in step.Inputs.OrderBy(value => value.Port))
                    {
                        inputSubmitted = true;
                        InputOperationDto operation = await _client.RunInputAsync(
                            new InputTimelineRequest(input.Port, input.Timeline.ToArray()), watchdog.Token)
                            .ConfigureAwait(false);
                        if (operation.Port != input.Port || operation.TotalFrames != input.TotalFrames ||
                            operation.StartsAfterFrame < 0)
                            throw new InvalidDataException("The input operation response did not match its request.");
                        operations.Add(new ScenarioInputOperation(step.Id, operation.Port, operation.StartsAfterFrame));
                    }
                }

                NeutralResult neutral = await WaitForNeutralAsync(deadlineStart, step.Checkpoint.TimeoutFrames,
                    watchdog.Token).ConfigureAwait(false);
                finalFrame = neutral.Frame;
                if (!neutral.Passed)
                {
                    failedCheckpoint = step.Id;
                    unmet = [new ScenarioUnmetPredicate(-1, "input.neutral", neutral.Error!)];
                    primaryError = neutral.Error;
                    outcome = ScenarioRunOutcome.Failed;
                    goto Complete;
                }

                CheckpointResult checkpoint = await WaitForCheckpointAsync(scenario, step.Id,
                    step.Checkpoint, watchdog.Token, deadlineStart, postResetDiagnostics, metricBaseline)
                    .ConfigureAwait(false);
                finalFrame = checkpoint.Frame;
                finalDiagnostics = checkpoint.Diagnostics;
                if (!checkpoint.Passed)
                {
                    failedCheckpoint = step.Id;
                    unmet = checkpoint.Unmet;
                    primaryError = checkpoint.Error;
                    outcome = ScenarioRunOutcome.Failed;
                    goto Complete;
                }
            }

            outcome = ScenarioRunOutcome.Passed;
            goto Complete;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = ScenarioRunOutcome.Cancelled;
            primaryError = "Scenario execution was cancelled.";
            goto Complete;
        }
        catch (OperationCanceledException)
        {
            outcome = ScenarioRunOutcome.Failed;
            primaryError = "Scenario execution exceeded its wall-clock deadline.";
            goto Complete;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
        {
            outcome = inputSubmitted ? ScenarioRunOutcome.Indeterminate : ScenarioRunOutcome.Failed;
            primaryError = inputSubmitted
                ? "The automation connection was interrupted after input submission."
                : "The automation response was invalid or interrupted.";
            goto Complete;
        }
        catch (Exception)
        {
            outcome = inputSubmitted ? ScenarioRunOutcome.Indeterminate : ScenarioRunOutcome.Failed;
            primaryError = inputSubmitted
                ? "Scenario execution failed after input submission; the game outcome is unknown."
                : "Scenario execution failed before input submission.";
            goto Complete;
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(CleanupTimeout);
            try
            {
                OperationResultDto cleared = await _client.ClearInputAsync(cleanup.Token).ConfigureAwait(false);
                cleanupSucceeded = cleared.Applied;
                if (cleanupSucceeded)
                {
                    CombinedTelemetryDto telemetry = await _client.GetTelemetryAsync(cleanup.Token)
                        .ConfigureAwait(false);
                    finalFrame = Math.Max(finalFrame ?? telemetry.Runtime.Frame, telemetry.Runtime.Frame);
                    cleanupVerified = IsNeutral(telemetry.Game.Input);
                }
            }
            catch
            {
                cleanupSucceeded = false;
            }
            if (outcome == ScenarioRunOutcome.Passed && (!cleanupSucceeded || !cleanupVerified))
            {
                outcome = ScenarioRunOutcome.CleanupFailed;
                primaryError = "Scenario input cleanup could not be verified.";
            }
        }

    Complete:
        return Result();

        ScenarioRunResult Result() => new(outcome, failedCheckpoint, unmet.Take(16).ToArray(),
            initialFrame, finalFrame, operations.Take(64).ToArray(), initialDiagnostics, postResetDiagnostics,
            finalDiagnostics, true, cleanupSucceeded, cleanupVerified, primaryError);
    }

    private async Task<CheckpointResult> WaitForCheckpointAsync(ScenarioDefinition scenario, string name,
        ScenarioCheckpoint checkpoint, CancellationToken token, long? startFrame = null,
        ScenarioDiagnosticIdentity? expectedIdentity = null, Envelope? metricBaseline = null)
    {
        long baseline = startFrame ?? -1;
        long lastFrame = -1;
        int stagnantPolls = 0;
        IReadOnlyList<ScenarioUnmetPredicate> unmet = [];
        ScenarioDiagnosticIdentity? observedIdentity = expectedIdentity;
        while (true)
        {
            CombinedTelemetryDto telemetry = await _client.GetTelemetryAsync(token).ConfigureAwait(false);
            long frame = telemetry.Runtime.Frame;
            if (baseline < 0) baseline = frame;

            ModDiagnosticsDto diagnostics = await _client.CaptureModDiagnosticsAsync(
                new ModDiagnosticsCaptureRequest(scenario.ModId), token).ConfigureAwait(false);
            Envelope envelope;
            try
            {
                envelope = ValidateEnvelope(diagnostics, scenario.ModId);
            }
            catch (InvalidDataException exception)
            {
                return new CheckpointResult(false, frame, null, null,
                    [new ScenarioUnmetPredicate(-1, "diagnostics.envelope", exception.Message)],
                    "The diagnostic envelope was malformed.");
            }
            long observationFrame = Math.Max(frame, diagnostics.Frame);
            if (diagnostics.Frame < frame || (lastFrame >= 0 && observationFrame < lastFrame))
                return new CheckpointResult(false, observationFrame, envelope.Identity, envelope,
                    [new ScenarioUnmetPredicate(-1, "diagnostics.frame", "diagnostic frame regressed")],
                    $"Checkpoint '{name}' observed a non-monotonic diagnostic frame.");
            if (baseline < 0) baseline = observationFrame;
            stagnantPolls = observationFrame > lastFrame ? 0 : stagnantPolls + 1;
            lastFrame = Math.Max(lastFrame, observationFrame);
            if (observedIdentity is not null &&
                (envelope.Identity.SessionId != observedIdentity.SessionId ||
                 envelope.Identity.Generation != observedIdentity.Generation))
            {
                return new CheckpointResult(false, observationFrame, envelope.Identity, envelope,
                    [new ScenarioUnmetPredicate(-1, "diagnostics.identity", "diagnostic identity changed")],
                    "The diagnostic identity changed during scenario execution.");
            }
            observedIdentity ??= envelope.Identity;

            Evaluation evaluation = Evaluate(checkpoint.Predicates, telemetry.Game, envelope, metricBaseline);
            if (evaluation.TerminalFailure)
                return new CheckpointResult(false, observationFrame, envelope.Identity, envelope, evaluation.Unmet,
                    $"Checkpoint '{name}' reported a terminal diagnostic failure.");
            if (evaluation.Unmet.Count == 0 && observationFrame - baseline <= checkpoint.TimeoutFrames)
                return new CheckpointResult(true, observationFrame, envelope.Identity, envelope, [], null);
            unmet = evaluation.Unmet;
            if (observationFrame - baseline >= checkpoint.TimeoutFrames)
            {
                if (unmet.Count == 0)
                    unmet = [new ScenarioUnmetPredicate(-1, "checkpoint.deadline", "predicate matched too late")];
                return new CheckpointResult(false, observationFrame, envelope.Identity, envelope, unmet,
                    $"Checkpoint '{name}' exceeded its frame deadline.");
            }
            if (stagnantPolls >= MaximumStagnantPolls)
                return new CheckpointResult(false, observationFrame, envelope.Identity, envelope, unmet,
                    $"Checkpoint '{name}' observed no automation frame progress.");
            await _clock.DelayAsync(PollDelay, token).ConfigureAwait(false);
        }
    }

    private async Task<NeutralResult> WaitForNeutralAsync(long baseline, int timeoutFrames,
        CancellationToken token)
    {
        long lastFrame = -1;
        int stagnantPolls = 0;
        while (true)
        {
            CombinedTelemetryDto telemetry = await _client.GetTelemetryAsync(token).ConfigureAwait(false);
            long frame = telemetry.Runtime.Frame;
            if (lastFrame >= 0 && frame < lastFrame)
                return new NeutralResult(false, frame, "Input telemetry reported a non-monotonic automation frame.");
            if (IsNeutral(telemetry.Game.Input) && frame - baseline <= timeoutFrames)
                return new NeutralResult(true, frame, null);
            stagnantPolls = frame > lastFrame ? 0 : stagnantPolls + 1;
            lastFrame = Math.Max(lastFrame, frame);
            if (frame - baseline >= timeoutFrames)
                return new NeutralResult(false, frame, "Input remained active through the frame deadline.");
            if (stagnantPolls >= MaximumStagnantPolls)
                return new NeutralResult(false, frame, "Input remained active without automation frame progress.");
            await _clock.DelayAsync(PollDelay, token).ConfigureAwait(false);
        }
    }

    private static bool IsNeutral(InputTelemetryDto? input) => input is not null &&
        input.AutomationMask1 == 0 && input.AutomationMask2 == 0 &&
        input.AutomationFrames1 == 0 && input.AutomationFrames2 == 0;

    private static Evaluation Evaluate(IReadOnlyList<ScenarioPredicate> predicates, GameTelemetryDto game,
        Envelope diagnostics, Envelope? metricBaseline)
    {
        var unmet = new List<ScenarioUnmetPredicate>();
        bool terminal = false;
        for (int index = 0; index < predicates.Count; index++)
        {
            ScenarioPredicate predicate = predicates[index];
            if (predicate is GameScenarioPredicate gamePredicate)
            {
                (bool matches, string observed) = EvaluateGame(gamePredicate, game);
                if (!matches) unmet.Add(new ScenarioUnmetPredicate(index, Describe(gamePredicate), observed));
                continue;
            }

            if (predicate is MetricScenarioPredicate metric)
            {
                EvaluateMetric(index, metric, diagnostics, metricBaseline, unmet, ref terminal);
                continue;
            }

            var diagnostic = (DiagnosticScenarioPredicate)predicate;
            if (diagnostics.Schema != diagnostic.Schema)
            {
                unmet.Add(new ScenarioUnmetPredicate(index, Describe(diagnostic), "diagnostic schema mismatch"));
                terminal = true;
                continue;
            }
            if (!diagnostics.Fields.TryGetValue(diagnostic.Field, out string? value))
            {
                unmet.Add(new ScenarioUnmetPredicate(index, Describe(diagnostic), "diagnostic field missing"));
                terminal = true;
                continue;
            }
            if (diagnostic.ExactValue is not null)
            {
                if (value != diagnostic.ExactValue)
                    unmet.Add(new ScenarioUnmetPredicate(index, Describe(diagnostic), Bound(value)));
                continue;
            }
            if (value.Length == 0 || value[0] is not ('P' or 'W' or 'F'))
            {
                unmet.Add(new ScenarioUnmetPredicate(index, Describe(diagnostic), "malformed diagnostic result"));
                terminal = true;
                continue;
            }
            if (value[0] == 'F')
            {
                unmet.Add(new ScenarioUnmetPredicate(index, Describe(diagnostic), Bound(value)));
                terminal = true;
                continue;
            }
            char expected = diagnostic.Result == DiagnosticResult.Pass ? 'P' : 'W';
            if (value[0] != expected)
                unmet.Add(new ScenarioUnmetPredicate(index, Describe(diagnostic), Bound(value)));
        }
        return new Evaluation(unmet, terminal);
    }

    private static (bool Matches, string Observed) EvaluateGame(GameScenarioPredicate predicate,
        GameTelemetryDto game)
    {
        return predicate.Field switch
        {
            GamePredicateField.State => (game.State == predicate.Expected.String, Bound(game.State)),
            GamePredicateField.Stage => (game.Stage == predicate.Expected.String, Bound(game.Stage)),
            GamePredicateField.Character => (game.Character == predicate.Expected.String, Bound(game.Character)),
            GamePredicateField.GameStepRaw => (predicate.Expected.UnsignedInteger is uint gameStep &&
                game.GameStepRaw == gameStep,
                game.GameStepRaw.ToString()),
            GamePredicateField.EngineStepRaw => (predicate.Expected.UnsignedInteger is uint engineStep &&
                game.EngineStepRaw == engineStep,
                game.EngineStepRaw.ToString()),
            GamePredicateField.Loading => (predicate.Expected.Boolean is bool loading && game.Loading == loading,
                game.Loading.ToString()),
            GamePredicateField.MenuOpen => (predicate.Expected.Boolean is bool menu && game.MenuOpen == menu,
                game.MenuOpen.ToString()),
            GamePredicateField.MapOpen => (predicate.Expected.Boolean is bool map && game.MapOpen == map,
                game.MapOpen.ToString()),
            GamePredicateField.PlayerHasControl => (predicate.Expected.Boolean is bool control &&
                game.Player?.HasControl == control,
                game.Player?.HasControl.ToString() ?? "unavailable"),
            GamePredicateField.Area => CompareGameInteger(predicate, game.Area),
            GamePredicateField.Room => CompareGameInteger(predicate, game.Room),
            GamePredicateField.RoomX => CompareGameInteger(predicate, game.RoomX),
            GamePredicateField.RoomY => CompareGameInteger(predicate, game.RoomY),
            _ => (false, "unsupported game field"),
        };
    }

    private static (bool Matches, string Observed) CompareGameInteger(GameScenarioPredicate predicate, int value) =>
        (predicate.Expected.SignedInteger == value, value.ToString());

    private static void EvaluateMetric(int index, MetricScenarioPredicate predicate, Envelope diagnostics,
        Envelope? baseline, List<ScenarioUnmetPredicate> unmet, ref bool terminal)
    {
        if (diagnostics.Schema != predicate.Schema)
        {
            unmet.Add(new ScenarioUnmetPredicate(index, Describe(predicate), "diagnostic schema mismatch"));
            terminal = true;
            return;
        }
        if (!diagnostics.Metrics.TryGetValue(predicate.Name, out MetricValue? observed) || observed is null)
        {
            unmet.Add(new ScenarioUnmetPredicate(index, Describe(predicate), "metric missing"));
            terminal = true;
            return;
        }
        if (observed.Type != predicate.ScalarType)
        {
            unmet.Add(new ScenarioUnmetPredicate(index, Describe(predicate), "metric scalar type mismatch"));
            terminal = true;
            return;
        }
        bool delta = predicate.Operator is MetricOperator.DeltaEq or MetricOperator.DeltaGte or
            MetricOperator.DeltaLte;
        long? integer = observed.Integer;
        if (delta)
        {
            if (baseline is null || baseline.Identity.SessionId != diagnostics.Identity.SessionId ||
                baseline.Identity.Generation != diagnostics.Identity.Generation ||
                !baseline.Metrics.TryGetValue(predicate.Name, out MetricValue? initial) ||
                initial.Type != MetricScalarType.Integer || initial.Integer is not long start ||
                observed.Integer is not long current)
            {
                unmet.Add(new ScenarioUnmetPredicate(index, Describe(predicate), "metric delta baseline unavailable or mismatched"));
                terminal = true;
                return;
            }
            try { integer = checked(current - start); }
            catch (OverflowException)
            {
                unmet.Add(new ScenarioUnmetPredicate(index, Describe(predicate), "metric delta overflow"));
                terminal = true;
                return;
            }
        }
        bool matches = predicate.ScalarType switch
        {
            MetricScalarType.Integer => Compare(predicate.Operator, integer, predicate.Expected.SignedInteger),
            MetricScalarType.Boolean => CompareBoolean(predicate.Operator, observed.Boolean, predicate.Expected.Boolean),
            MetricScalarType.String => CompareString(predicate.Operator, observed.String, predicate.Expected.String),
            _ => false,
        };
        if (!matches) unmet.Add(new ScenarioUnmetPredicate(index, Describe(predicate), Bound(observed.Display)));
    }

    private static bool Compare(MetricOperator operation, long? actual, long? expected) =>
        actual is long a && expected is long e && operation switch
        {
            MetricOperator.Eq => a == e,
            MetricOperator.Ne => a != e,
            MetricOperator.Gte => a >= e,
            MetricOperator.Lte => a <= e,
            MetricOperator.DeltaEq => a == e,
            MetricOperator.DeltaGte => a >= e,
            MetricOperator.DeltaLte => a <= e,
            _ => false,
        };

    private static bool CompareBoolean(MetricOperator operation, bool? actual, bool? expected) =>
        actual is bool a && expected is bool e && operation switch
        {
            MetricOperator.Eq => a == e,
            MetricOperator.Ne => a != e,
            _ => false,
        };

    private static bool CompareString(MetricOperator operation, string? actual, string? expected) =>
        actual is not null && expected is not null && operation switch
        {
            MetricOperator.Eq => actual == expected,
            MetricOperator.Ne => actual != expected,
            _ => false,
        };

    private static Envelope ValidateEnvelope(ModDiagnosticsDto diagnostics, string modId)
    {
        if (diagnostics.Payload.ValueKind == JsonValueKind.Object &&
            diagnostics.Payload.TryGetProperty("schema", out JsonElement strictSchema) &&
            strictSchema.ValueKind == JsonValueKind.String && strictSchema.GetString() == "p2d4/2")
        {
            P2D4Envelope parsed = P2D4EnvelopeParser.Parse(diagnostics, modId);
            return new(parsed.Schema, parsed.Fields, parsed.Metrics.ToDictionary(pair => pair.Key,
                pair => new MetricValue(pair.Value.Type, pair.Value.Integer, pair.Value.Boolean,
                    pair.Value.String, pair.Value.Display), StringComparer.Ordinal), parsed.Identity);
        }
        if (diagnostics.Id != modId || diagnostics.Frame < 0 || diagnostics.Generation < 0 ||
            diagnostics.SessionId.Length != 32 || diagnostics.SessionId.Any(value => !Uri.IsHexDigit(value)) ||
            diagnostics.Payload.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Diagnostic identity or envelope is invalid.");
        JsonElement payload = diagnostics.Payload;
        if (!payload.TryGetProperty("schema", out JsonElement schemaElement) ||
            schemaElement.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(schemaElement.GetString()) ||
            !payload.TryGetProperty("sessionId", out JsonElement sessionElement) ||
            sessionElement.ValueKind != JsonValueKind.String || sessionElement.GetString() != diagnostics.SessionId ||
            !payload.TryGetProperty("generation", out JsonElement generationElement) ||
            !generationElement.TryGetInt32(out int generation) || generation != diagnostics.Generation ||
            !payload.TryGetProperty("automationFrame", out JsonElement frameElement) ||
            !frameElement.TryGetInt64(out long frame) || frame != diagnostics.Frame ||
            !payload.TryGetProperty("fields", out JsonElement fieldsElement) ||
            fieldsElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Diagnostic envelope identity does not match its transport identity.");

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty field in fieldsElement.EnumerateObject())
        {
            if (field.Value.ValueKind != JsonValueKind.String || !fields.TryAdd(field.Name, field.Value.GetString()!))
                throw new InvalidDataException("Diagnostic fields must be unique strings.");
        }
        string schema = schemaElement.GetString()!;
        var metrics = new Dictionary<string, MetricValue>(StringComparer.Ordinal);
        return new Envelope(schema, fields, metrics,
            new ScenarioDiagnosticIdentity(diagnostics.SessionId, diagnostics.Generation, diagnostics.Frame,
                schema));
    }

    private static MetricValue ParseMetric(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt64(out long integer) =>
            new MetricValue(MetricScalarType.Integer, integer, null, null, integer.ToString()),
        JsonValueKind.True => new MetricValue(MetricScalarType.Boolean, null, true, null, "true"),
        JsonValueKind.False => new MetricValue(MetricScalarType.Boolean, null, false, null, "false"),
        JsonValueKind.String when value.GetString() is { } text && text.Length <= 256 &&
            text.All(character => character is >= ' ' and <= '~') =>
            new MetricValue(MetricScalarType.String, null, null, text, text),
        _ => throw new InvalidDataException("Diagnostic metric must be a bounded integer, boolean, or printable ASCII string."),
    };

    private static string Describe(GameScenarioPredicate predicate) => $"game.{predicate.Field}";
    private static string Describe(DiagnosticScenarioPredicate predicate) => $"diagnostic.{predicate.Field}";
    private static string Describe(MetricScenarioPredicate predicate) => $"metric.{predicate.Name}.{predicate.Operator}";
    private static string Bound(string? value) => value is null ? "null" : value[..Math.Min(value.Length, 256)];

    private sealed record Envelope(string Schema, IReadOnlyDictionary<string, string> Fields,
        IReadOnlyDictionary<string, MetricValue> Metrics,
        ScenarioDiagnosticIdentity Identity);
    private sealed record MetricValue(MetricScalarType Type, long? Integer, bool? Boolean, string? String,
        string Display);
    private sealed record Evaluation(IReadOnlyList<ScenarioUnmetPredicate> Unmet, bool TerminalFailure);
    private sealed record CheckpointResult(bool Passed, long Frame, ScenarioDiagnosticIdentity? Diagnostics,
        Envelope? Envelope,
        IReadOnlyList<ScenarioUnmetPredicate> Unmet, string? Error);
    private sealed record NeutralResult(bool Passed, long Frame, string? Error);
}
