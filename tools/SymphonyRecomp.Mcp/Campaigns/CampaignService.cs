using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using SymphonyRecomp.Automation.Contracts;
using SymphonyRecomp.Mcp.Scenarios;

namespace SymphonyRecomp.Mcp.Campaigns;

public enum CampaignOutcome { Running, Passed, Failed, Cancelled }

public sealed record CampaignProgress(int Accepted, int Required, int? ExpectedFrom, int? ExpectedTo,
    int? LastRoom, int ElapsedSeconds, int RequiredSeconds, int SamplesCompleted, int SamplesRequired);
public sealed record CampaignCleanup(bool Attempted, bool Succeeded, bool Verified);
public sealed record CampaignStatus(string Schema, string? RunId, string? CatalogId, string? CatalogVersion,
    string? EvidenceVersion, string? BuildVersion, int? ProcessId, string? ModId, string? ModVersion,
    string? SessionId, int? Generation, DateTimeOffset? StartedUtc, DateTimeOffset? UpdatedUtc,
    DateTimeOffset? EndedUtc, double ElapsedSeconds, string Outcome, string? FirstFailedCheckpoint,
    string? Reason, CampaignProgress Progress, long? LastStateFrame, long? LastDiagnosticFrame,
    CampaignCleanup Cleanup, string? ArtifactId);

public interface ICampaignClock
{
    DateTimeOffset UtcNow { get; }
    TimeSpan Elapsed { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken token);
}

public sealed class SystemCampaignClock : ICampaignClock
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public TimeSpan Elapsed => _watch.Elapsed;
    public Task DelayAsync(TimeSpan delay, CancellationToken token) => Task.Delay(delay, token);
}

internal interface ICampaignArtifactWriter
{
    void WriteAtomic(string path, ReadOnlySpan<byte> bytes);
}

internal sealed class CampaignArtifactWriter : ICampaignArtifactWriter
{
    public void WriteAtomic(string path, ReadOnlySpan<byte> bytes)
    {
        string temporary = path + ".tmp";
        if (OperatingSystem.IsWindows()) File.WriteAllBytes(temporary, bytes);
        else
        {
            using var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            stream.Write(bytes);
        }
        File.Move(temporary, path, true);
        CampaignService.MakeFilePrivate(path);
    }
}

public sealed class CampaignService : IHostedService, IAsyncDisposable
{
    public const string StatusSchema = "sotn-campaign-status/1";
    public const string ManifestSchema = "sotn-campaign-artifacts/1";
    private const string TargetMod = "coop-feasibility";
    internal const long MaximumExactOwnedAttackLifetime = 48;
    private readonly object _sync = new();
    private readonly IScenarioAutomationClient _client;
    private readonly CampaignCatalog _catalog;
    private readonly ScenarioExecutionGate _gate;
    private readonly ICampaignClock _clock;
    private readonly string _root;
    private readonly Func<string> _idFactory;
    private readonly ICampaignArtifactWriter _writer;
    private CampaignRun? _active;
    private CampaignStatus _status = EmptyStatus();
    private bool _stopping;

    public CampaignService(IScenarioAutomationClient client, CampaignCatalog catalog,
        ScenarioExecutionGate gate, ICampaignClock clock)
        : this(client, catalog, gate, clock, ResolveRoot(), DefaultId, new CampaignArtifactWriter()) { }

    internal CampaignService(IScenarioAutomationClient client, CampaignCatalog catalog,
        ScenarioExecutionGate gate, ICampaignClock clock, string root, Func<string> idFactory,
        ICampaignArtifactWriter? writer = null)
    {
        _client = client;
        _catalog = catalog;
        _gate = gate;
        _clock = clock;
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _idFactory = idFactory;
        _writer = writer ?? new CampaignArtifactWriter();
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<CampaignStatus> StartCampaignAsync(string id, bool confirm, CancellationToken callerToken)
    {
        // Catalog lookup intentionally precedes confirmation, bridge traffic, directory creation, and gate activity.
        CampaignDefinition definition = _catalog.Get(id);
        if (!confirm) throw new McpException("confirm must be true for this operation.");
        lock (_sync)
        {
            if (_stopping) throw new McpException("Campaign service is shutting down.");
            if (_active is { Task.IsCompleted: false }) throw new McpException("A campaign is already running.");
        }

        // The first check rejects already-active input without taking campaign ownership. The
        // second check occurs under the lifetime lease, closing the preflight/mutation race.
        await RequireNeutralAsync(callerToken).ConfigureAwait(false);
        IDisposable lease = _gate.TryEnterScenario();
        try
        {
            await RequireNeutralAsync(callerToken).ConfigureAwait(false);
            CampaignPreflight preflight = await PreflightAsync(definition, callerToken).ConfigureAwait(false);
            string runId = _idFactory();
            ValidateId(runId);
            string directory = CreateRunDirectory(runId);
            var cancellation = new CancellationTokenSource();
            var run = new CampaignRun(runId, definition, preflight, directory, lease, cancellation,
                _clock.Elapsed, _clock.UtcNow);
            lock (_sync)
            {
                if (_stopping || _active is { Task.IsCompleted: false })
                    throw new McpException("A campaign became unavailable before start.");
                _active = run;
                _status = MakeStatus(run, CampaignOutcome.Running, null, null, null, run.ArtifactId,
                    new(false, false, false));
                run.Task = Task.Run(() => ExecuteAsync(run), CancellationToken.None);
            }
            lease = null!; // ownership transferred to CampaignRun
            return GetStatus();
        }
        catch
        {
            lease?.Dispose();
            throw;
        }
    }

    public CampaignStatus GetStatus()
    {
        lock (_sync) return _status;
    }

    public async Task<CampaignStatus> CancelAsync(bool confirm, CancellationToken callerToken)
    {
        if (!confirm) throw new McpException("confirm must be true for this operation.");
        CampaignRun? run;
        lock (_sync)
        {
            run = _active;
            if (run is null || run.Task.IsCompleted) return _status;
            run.Cancellation.Cancel();
        }
        try { await run.Task.WaitAsync(TimeSpan.FromSeconds(8), callerToken).ConfigureAwait(false); }
        catch (TimeoutException) { throw new McpException("Campaign cancellation is still being finalized."); }
        return GetStatus();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CampaignRun? run;
        lock (_sync) { _stopping = true; run = _active; run?.Cancellation.Cancel(); }
        if (run is not null)
        {
            await run.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await StopAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException exception)
        {
            throw new InvalidOperationException("Campaign disposal could not complete within its shutdown budget.", exception);
        }
    }

    private async Task RequireNeutralAsync(CancellationToken token)
    {
        BridgeStatusDto bridge = await _client.GetBridgeStatusAsync(token).ConfigureAwait(false);
        CombinedTelemetryDto telemetry = await _client.GetTelemetryAsync(token).ConfigureAwait(false);
        if (bridge.InputActive || !IsNeutral(telemetry.Game.Input))
            throw new McpException("Campaign start requires neutral automation input on both ports.");
    }

    private async Task<CampaignPreflight> PreflightAsync(CampaignDefinition definition, CancellationToken token)
    {
        BridgeStatusDto bridge = await _client.GetBridgeStatusAsync(token).ConfigureAwait(false);
        if (!bridge.Ready || bridge.ProtocolVersion != AutomationProtocol.Version || bridge.ProtocolVersion != "1.2")
            throw new McpException("Campaign preflight requires a ready automation protocol 1.2 bridge.");
        ModTelemetryDto? mod = (await _client.ListModsAsync(token).ConfigureAwait(false))
            .SingleOrDefault(value => value.Id == TargetMod && value.Loaded);
        if (mod is null) throw new McpException("Campaign preflight requires loaded coop-feasibility.");
        CombinedTelemetryDto state = await _client.GetTelemetryAsync(token).ConfigureAwait(false);
        ModDiagnosticsDto diagnostics = await _client.CaptureModDiagnosticsAsync(
            new(TargetMod), token).ConfigureAwait(false);
        DiagnosticSnapshot snapshot = DiagnosticSnapshot.Parse(diagnostics);
        if (definition.Id == "coop-route-25") ValidateRouteAdmission(snapshot);
        ValidateIdentityAndSafety(state, diagnostics, snapshot, null, requireSampleState: true,
            requireQuiescentAttack: true);
        if (definition.Kind == CampaignKind.Route &&
            (state.Game.Stage != definition.Stage || state.Game.Area != definition.Area || state.Game.Room != definition.OrderedRooms[0]))
            throw new McpException("Route campaign must start in MarbleGallery area 40 room 140.");
        ScenarioBuildIdentity build = await _client.GetBuildIdentityAsync(token).ConfigureAwait(false);
        return new(state, diagnostics, snapshot, mod.Version, build.McpInformationalVersion);
    }

    private static void ValidateRouteAdmission(DiagnosticSnapshot snapshot)
    {
        if (snapshot.TransitionPending || snapshot.AwaitingPostTransitionMovement ||
            snapshot.TransitionCompleted != snapshot.TransitionPassed ||
            snapshot.PostTransitionCommandedPixels < 8 || !snapshot.PostTransitionMoved ||
            snapshot.Fatal || snapshot.ErrorCode != "0")
            throw new McpException("Route campaign requires a settled, passed P2 post-transition state.");
    }

    private async Task ExecuteAsync(CampaignRun run)
    {
        CampaignOutcome outcome = CampaignOutcome.Failed;
        string? checkpoint = null, reason = null;
        try
        {
            if (run.Definition.Kind == CampaignKind.Route) await ObserveRouteAsync(run).ConfigureAwait(false);
            else await ObserveSoakAsync(run).ConfigureAwait(false);
            outcome = CampaignOutcome.Passed;
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
        {
            outcome = CampaignOutcome.Cancelled;
            checkpoint = "cancelled";
            reason = "Campaign cancellation was requested.";
        }
        catch (CampaignFailure failure)
        {
            checkpoint = Bound(failure.Checkpoint, 64);
            reason = Bound(failure.Message, 256);
        }
        catch
        {
            checkpoint = "observer";
            reason = "The campaign observer encountered an internal failure.";
        }
        finally
        {
            if (outcome == CampaignOutcome.Failed)
            {
                await TryCaptureScreenshotAsync(run, "failure").ConfigureAwait(false);
                await TryCaptureFailureDetailsAsync(run).ConfigureAwait(false);
            }
            CampaignCleanup cleanup = await CleanupAsync().ConfigureAwait(false);
            if ((!cleanup.Succeeded || !cleanup.Verified) && outcome == CampaignOutcome.Passed)
            {
                outcome = CampaignOutcome.Failed;
                checkpoint ??= "cleanup";
                reason ??= "Automation input neutrality could not be verified.";
            }
            CampaignStatus finalStatus = MakeStatus(run, outcome, checkpoint, reason, _clock.UtcNow,
                run.ArtifactId, cleanup);
            if (outcome == CampaignOutcome.Passed && run.ArtifactErrors.Count != 0)
            {
                outcome = CampaignOutcome.Failed;
                checkpoint = "artifacts";
                reason = "A requested success artifact could not be persisted.";
                finalStatus = MakeStatus(run, outcome, checkpoint, reason, _clock.UtcNow,
                    run.ArtifactId, cleanup);
            }
            try
            {
                WriteManifest(run, finalStatus, final: true);
            }
            catch
            {
                outcome = CampaignOutcome.Failed;
                checkpoint = "artifacts";
                reason = "The final campaign manifest could not be persisted.";
                AddArtifactError(run, reason);
                finalStatus = MakeStatus(run, outcome, checkpoint, reason, _clock.UtcNow,
                    run.ArtifactId, cleanup);
                try { WriteManifest(run, finalStatus, final: true); } catch { }
            }
            lock (_sync) _status = finalStatus;
            run.Lease.Dispose();
            run.Cancellation.Dispose();
        }
    }

    private async Task ObserveRouteAsync(CampaignRun run)
    {
        var observations = new List<RouteTransitionObservation>(25);
        int lastRoom = run.Preflight.State.Game.Room;
        DiagnosticSnapshot baseline = run.Preflight.Snapshot;
        long lastAcceptedStateFrame = run.Preflight.State.Runtime.Frame;
        long lastAcceptedDiagnosticFrame = run.Preflight.Diagnostics.Frame;
        CaptureSummary(run, run.Preflight.State, run.Preflight.Diagnostics, baseline, "start");
        await TryCaptureScreenshotAsync(run, "start").ConfigureAwait(false);
        while (observations.Count < run.Definition.RequiredTransitions)
        {
            run.Cancellation.Token.ThrowIfCancellationRequested();
            EnsureDeadline(run);
            await _clock.DelayAsync(TimeSpan.FromMilliseconds(run.Definition.PollMilliseconds), run.Cancellation.Token).ConfigureAwait(false);
            ValidateBridge(await _client.GetBridgeStatusAsync(run.Cancellation.Token).ConfigureAwait(false));
            CombinedTelemetryDto state = await _client.GetTelemetryAsync(run.Cancellation.Token).ConfigureAwait(false);
            if (state.Game.Stage != run.Definition.Stage || state.Game.Area != run.Definition.Area)
                throw new CampaignFailure("route", "The route left MarbleGallery area 40.");
            if (state.Game.Room == lastRoom) continue;
            int segment = observations.Count % (run.Definition.OrderedRooms.Count - 1);
            int expectedFrom = run.Definition.OrderedRooms[segment], expectedTo = run.Definition.OrderedRooms[segment + 1];
            if (lastRoom != expectedFrom || state.Game.Room != expectedTo)
                throw new CampaignFailure("route", "An unexpected room or route segment was observed.");
            if (state.Runtime.Frame <= lastAcceptedStateFrame)
                throw new CampaignFailure("frame", "State frames did not advance monotonically.");
            ModDiagnosticsDto diagnostics;
            DiagnosticSnapshot current;
            TimeSpan settleStarted = _clock.Elapsed;
            while (true)
            {
                diagnostics = await _client.CaptureModDiagnosticsAsync(new(TargetMod), run.Cancellation.Token).ConfigureAwait(false);
                current = DiagnosticSnapshot.Parse(diagnostics);
                ValidateIdentityAndSafety(state, diagnostics, current, run.Preflight, requireSampleState: false,
                    requireQuiescentAttack: true);
                long completedDelta = current.TransitionCompleted - baseline.TransitionCompleted;
                long passedDelta = current.TransitionPassed - baseline.TransitionPassed;
                long reconstructionDelta = current.ReconstructionSuccesses - baseline.ReconstructionSuccesses;
                bool stablePlay = state.Game.Available && state.Game.State == "Play" && state.Game.Character == "Alucard" &&
                    !state.Game.Loading && !state.Game.MenuOpen && !state.Game.MapOpen;
                bool accepted = stablePlay && completedDelta == 1 && passedDelta == 1 && reconstructionDelta >= 1 &&
                    current.PostTransitionCommandedPixels >= 8 && current.PostTransitionMoved && !current.TransitionPending &&
                    !current.AwaitingPostTransitionMovement;
                bool terminalMetricFailure = completedDelta > 1 || passedDelta > 1 || completedDelta < 0 ||
                    passedDelta < 0 || reconstructionDelta < 0 ||
                    current.PostTransitionAbandonments != baseline.PostTransitionAbandonments ||
                    current.TransitionReconstructionFailures != baseline.TransitionReconstructionFailures ||
                    current.ReconstructionFailures != baseline.ReconstructionFailures;
                if (terminalMetricFailure)
                    throw new CampaignFailure("transition-metrics",
                        $"Transition metrics violated the exact route contract: completedDelta={completedDelta} passedDelta={passedDelta} reconstructionDelta={reconstructionDelta} currentPixels={current.PostTransitionCommandedPixels} abandonments={current.PostTransitionAbandonments - baseline.PostTransitionAbandonments} transitionReconstructionFailures={current.TransitionReconstructionFailures - baseline.TransitionReconstructionFailures} reconstructionFailures={current.ReconstructionFailures - baseline.ReconstructionFailures}.");
                if (accepted) break;
                if ((_clock.Elapsed - settleStarted).TotalSeconds > 10)
                    throw new CampaignFailure("transition-metrics", "Transition metrics did not settle before the bounded deadline.");
                await _clock.DelayAsync(TimeSpan.FromMilliseconds(run.Definition.PollMilliseconds), run.Cancellation.Token).ConfigureAwait(false);
                state = await _client.GetTelemetryAsync(run.Cancellation.Token).ConfigureAwait(false);
                if (state.Game.Room != expectedTo)
                    throw new CampaignFailure("route", "The room changed before transition evidence was accepted.");
            }
            if (diagnostics.Frame <= lastAcceptedDiagnosticFrame)
                throw new CampaignFailure("frame", "Diagnostic frames did not advance monotonically.");
            observations.Add(new(lastRoom, state.Game.Room, true));
            RouteAggregateArtifact aggregate = new RouteAggregateExecutor().Execute(
                RouteAggregateCatalog.No0MarbleGallery25, observations);
            if (aggregate.Outcome == "failed") throw new CampaignFailure("route-aggregate", "The route aggregate rejected an observation.");
            lastRoom = state.Game.Room;
            baseline = current;
            lastAcceptedStateFrame = state.Runtime.Frame;
            lastAcceptedDiagnosticFrame = diagnostics.Frame;
            run.Accepted = observations.Count;
            run.LastRoom = lastRoom;
            run.LastStateFrame = state.Runtime.Frame;
            run.LastDiagnosticFrame = diagnostics.Frame;
            CaptureSummary(run, state, diagnostics, current, $"transition-{run.Accepted}");
            if (run.Accepted == 13) await TryCaptureScreenshotAsync(run, "midpoint").ConfigureAwait(false);
            UpdateRunning(run);
        }
        RouteAggregateArtifact final = new RouteAggregateExecutor().Execute(RouteAggregateCatalog.No0MarbleGallery25, observations);
        if (final.Outcome != "passed") throw new CampaignFailure("route-aggregate", "The route aggregate remained incomplete.");
        await TryCaptureScreenshotAsync(run, "finish").ConfigureAwait(false);
    }

    private async Task ObserveSoakAsync(CampaignRun run)
    {
        TimeSpan started = run.StartElapsed;
        long lastFrame = run.Preflight.State.Runtime.Frame;
        long lastDiagnosticFrame = run.Preflight.Diagnostics.Frame;
        TimeSpan lastFrameProgress = _clock.Elapsed;
        TrackTransientAttack(run, run.Preflight.Diagnostics.Frame, run.Preflight.Snapshot);
        await AcceptSoakSampleAsync(run, run.Preflight.State, run.Preflight.Diagnostics, run.Preflight.Snapshot, 0).ConfigureAwait(false);
        while ((_clock.Elapsed - started).TotalSeconds < run.Definition.RequiredSeconds)
        {
            run.Cancellation.Token.ThrowIfCancellationRequested();
            EnsureDeadline(run);
            await _clock.DelayAsync(TimeSpan.FromMilliseconds(run.Definition.PollMilliseconds), run.Cancellation.Token).ConfigureAwait(false);
            ValidateBridge(await _client.GetBridgeStatusAsync(run.Cancellation.Token).ConfigureAwait(false));
            CombinedTelemetryDto state = await _client.GetTelemetryAsync(run.Cancellation.Token).ConfigureAwait(false);
            ModDiagnosticsDto diagnostics = await _client.CaptureModDiagnosticsAsync(new(TargetMod), run.Cancellation.Token).ConfigureAwait(false);
            DiagnosticSnapshot snapshot = DiagnosticSnapshot.Parse(diagnostics);
            ValidateIdentityAndSafety(state, diagnostics, snapshot, run.Preflight, requireSampleState: false,
                requireQuiescentAttack: false);
            TrackTransientAttack(run, diagnostics.Frame, snapshot);
            if (state.Runtime.Frame > lastFrame) { lastFrame = state.Runtime.Frame; lastFrameProgress = _clock.Elapsed; }
            else if ((_clock.Elapsed - lastFrameProgress).TotalSeconds > run.Definition.NoProgressSeconds)
                throw new CampaignFailure("frame-progress", "Game frames stopped progressing for too long.");
            if (diagnostics.Frame < lastDiagnosticFrame)
                throw new CampaignFailure("frame", "Diagnostic frames moved backward.");
            lastDiagnosticFrame = diagnostics.Frame;
            int due = Math.Min(run.Definition.SampleCount - 2,
                (int)((_clock.Elapsed - started).TotalSeconds / run.Definition.SampleSeconds));
            while (run.SamplesCompleted <= due)
            {
                ValidateIdentityAndSafety(state, diagnostics, snapshot, run.Preflight, requireSampleState: true,
                    requireQuiescentAttack: false);
                await AcceptSoakSampleAsync(run, state, diagnostics, snapshot, run.SamplesCompleted).ConfigureAwait(false);
            }
            run.LastStateFrame = state.Runtime.Frame;
            run.LastDiagnosticFrame = diagnostics.Frame;
            UpdateRunning(run);
        }
        await CaptureFinalSoakSampleIfNeededAsync(run).ConfigureAwait(false);
        if (run.SamplesCompleted != 13) throw new CampaignFailure("samples", "The soak did not capture all 13 scheduled samples.");
    }

    private async Task CaptureFinalSoakSampleIfNeededAsync(CampaignRun run)
    {
        if (run.SamplesCompleted >= run.Definition.SampleCount) return;
        ValidateBridge(await _client.GetBridgeStatusAsync(run.Cancellation.Token).ConfigureAwait(false));
        CombinedTelemetryDto state = await _client.GetTelemetryAsync(run.Cancellation.Token).ConfigureAwait(false);
        ModDiagnosticsDto diagnostics = await _client.CaptureModDiagnosticsAsync(
            new(TargetMod), run.Cancellation.Token).ConfigureAwait(false);
        DiagnosticSnapshot snapshot = DiagnosticSnapshot.Parse(diagnostics);
        ValidateIdentityAndSafety(state, diagnostics, snapshot, run.Preflight, requireSampleState: true,
            requireQuiescentAttack: false);
        if (state.Runtime.Frame < run.LastStateFrame || diagnostics.Frame < run.LastDiagnosticFrame)
            throw new CampaignFailure("frame", "The final soak sample frames moved backward.");
        TrackTransientAttack(run, diagnostics.Frame, snapshot);
        await AcceptSoakSampleAsync(run, state, diagnostics, snapshot,
            run.Definition.SampleCount - 1).ConfigureAwait(false);
    }

    private async Task AcceptSoakSampleAsync(CampaignRun run, CombinedTelemetryDto state,
        ModDiagnosticsDto diagnostics, DiagnosticSnapshot snapshot, int index)
    {
        CaptureSummary(run, state, diagnostics, snapshot, $"minute-{index * 5}");
        run.SamplesCompleted++;
        if (index is 0 or 6 or 12) await TryCaptureScreenshotAsync(run, $"minute-{index * 5}").ConfigureAwait(false);
        UpdateRunning(run);
    }

    private static void ValidateIdentityAndSafety(CombinedTelemetryDto state, ModDiagnosticsDto diagnostics,
        DiagnosticSnapshot snapshot, CampaignPreflight? identity, bool requireSampleState,
        bool requireQuiescentAttack)
    {
        if (state.Runtime.Frame < 0 || diagnostics.Frame < 0 || snapshot.Schema != "p2d4/2" ||
            diagnostics.Id != TargetMod || snapshot.Fatal || snapshot.ErrorCode != "0" || snapshot.KeyboardField != "-" ||
            snapshot.AttackOrphanMarkerCount != 0 || snapshot.AttackMarkerCount > 1 ||
            (requireQuiescentAttack && (snapshot.AttackMarkerCount != 0 || snapshot.AttackCleanupPending)) ||
            snapshot.AttackQuarantineSlot != -1 || snapshot.AttackFailures != 0 || snapshot.AttackTimingFailures != 0 ||
            snapshot.AttackEquipmentRestoreFailures != 0 || snapshot.CollisionRestoreFailures != 0 ||
            snapshot.VisualRestoreFailures != 0 || snapshot.ContactGuardFailures != 0 ||
            snapshot.HealthInvariantFailures != 0 || snapshot.DropTrackerFaulted)
            throw new CampaignFailure("safety", "A required co-op diagnostic safety invariant failed.");
        if (identity is not null && (diagnostics.SessionId != identity.Diagnostics.SessionId ||
            diagnostics.Generation != identity.Diagnostics.Generation ||
            state.Runtime.ProcessId != identity.State.Runtime.ProcessId))
            throw new CampaignFailure("identity", "The mod diagnostic session or generation changed.");
        if (requireSampleState && (!state.Game.Available || state.Game.State != "Play" ||
            state.Game.Character != "Alucard" || state.Game.Loading || state.Game.MenuOpen || state.Game.MapOpen))
            throw new CampaignFailure("game-state", "A sample was not in safe Play/Alucard gameplay.");
    }

    private static void TrackTransientAttack(CampaignRun run, long frame, DiagnosticSnapshot snapshot)
    {
        if (snapshot.AttackExactOwnedLifetimeCurrent < 0 ||
            snapshot.AttackExactOwnedLifetimeCurrent > snapshot.AttackExactOwnedLifetimeMaximum ||
            snapshot.AttackExactOwnedLifetimeMaximum > MaximumExactOwnedAttackLifetime)
            throw new CampaignFailure("attack-lifecycle",
                "Exact-owned attack lifetime exceeded the 40-window projectile lifecycle plus 8-window cleanup grace.");
        if (snapshot.AttackAllocations < run.LastAttackAllocations ||
            snapshot.AttackCleanups < run.LastAttackCleanups ||
            snapshot.AttackLifecycleCancellations < run.LastAttackLifecycleCancellations)
            throw new CampaignFailure("attack-lifecycle", "Attack lifecycle counters moved backward.");
        run.LastAttackAllocations = snapshot.AttackAllocations;
        run.LastAttackCleanups = snapshot.AttackCleanups;
        run.LastAttackLifecycleCancellations = snapshot.AttackLifecycleCancellations;
        bool active = snapshot.AttackMarkerCount == 1 || snapshot.AttackCleanupPending;
        if (!active) { run.AttackActiveSinceFrame = null; return; }
        run.AttackActiveSinceFrame ??= frame;
        if (frame < run.AttackActiveSinceFrame || frame - run.AttackActiveSinceFrame > 48)
            throw new CampaignFailure("attack-lifecycle",
                "An owned attack marker or cleanup remained active beyond the 40-frame projectile lifetime plus 8-frame cleanup grace.");
    }

    private static void ValidateBridge(BridgeStatusDto bridge)
    {
        if (!bridge.Ready || bridge.ProtocolVersion != "1.2")
            throw new CampaignFailure("bridge", "The automation bridge identity or readiness changed.");
    }

    private async Task<CampaignCleanup> CleanupAsync()
    {
        try
        {
            using var fresh = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            OperationResultDto cleared = await _client.ClearInputAsync(fresh.Token).ConfigureAwait(false);
            CombinedTelemetryDto state = await _client.GetTelemetryAsync(fresh.Token).ConfigureAwait(false);
            bool verified = IsNeutral(state.Game.Input);
            return new(true, cleared.Applied, verified);
        }
        catch { return new(true, false, false); }
    }

    private void CaptureSummary(CampaignRun run, CombinedTelemetryDto state, ModDiagnosticsDto diagnostics,
        DiagnosticSnapshot snapshot, string label)
    {
        if (run.Summaries.Count >= 32) run.Summaries.RemoveAt(0);
        byte[] stateBytes = JsonSerializer.SerializeToUtf8Bytes(new
        { state.Runtime.Frame, state.Runtime.ProcessId, state.Game.State, state.Game.Character, state.Game.Stage,
          state.Game.Area, state.Game.Room, state.Game.Loading, state.Game.MenuOpen, state.Game.MapOpen }, AutomationProtocol.Json);
        byte[] diagnosticBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, AutomationProtocol.Json);
        run.Summaries.Add(new(label, state.Runtime.Frame, diagnostics.Frame, state.Game.Room,
            Hash(stateBytes), Hash(diagnosticBytes), snapshot.TransitionCompleted, snapshot.TransitionPassed,
            snapshot.ReconstructionSuccesses, snapshot.PostTransitionCommandedPixels));
        run.LastStateFrame = state.Runtime.Frame;
        run.LastDiagnosticFrame = diagnostics.Frame;
        TryWriteManifest(run, final: false);
    }

    private async Task TryCaptureScreenshotAsync(CampaignRun run, string label)
    {
        if (run.Screenshots.Count >= 4) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            ScreenshotDto screenshot = await _client.CaptureScreenshotAsync(timeout.Token).ConfigureAwait(false);
            byte[] png = PngScreenshotValidator.DecodeAndValidate(screenshot);
            string name = $"screenshot-{label}.png";
            timeout.Token.ThrowIfCancellationRequested();
            _writer.WriteAtomic(Path.Combine(run.Directory, name), png);
            run.Screenshots.Add(new(name, screenshot.Frame, screenshot.Sha256, png.Length));
        }
        catch { AddArtifactError(run, "A scheduled screenshot could not be captured, validated, or persisted."); }
    }

    private async Task TryCaptureFailureDetailsAsync(CampaignRun run)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            EntityListDto entities = await _client.ListEntitiesAsync(128, timeout.Token).ConfigureAwait(false);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(entities with
            { Entities = entities.Entities.Take(128).ToArray() }, AutomationProtocol.Json);
            if (bytes.Length > 1024 * 1024) throw new InvalidDataException();
            timeout.Token.ThrowIfCancellationRequested();
            _writer.WriteAtomic(Path.Combine(run.Directory, "failure-entities.json"), bytes);
            run.FailureArtifacts.Add("failure-entities.json");
        }
        catch { AddArtifactError(run, "Bounded failure entities could not be captured."); }
        try
        {
            LogsResult logs = await _client.GetLogsAsync(100, timeout.Token).ConfigureAwait(false);
            var bounded = new LogsResult(logs.BridgeVersion,
                logs.BridgeLines.Take(100).Select(value => Bound(value, 4096)).ToArray(),
                logs.ProcessLines.Take(100).Select(value => Bound(value, 4096)).ToArray());
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(bounded, AutomationProtocol.Json);
            if (bytes.Length > 1024 * 1024) throw new InvalidDataException();
            timeout.Token.ThrowIfCancellationRequested();
            _writer.WriteAtomic(Path.Combine(run.Directory, "failure-logs.json"), bytes);
            run.FailureArtifacts.Add("failure-logs.json");
        }
        catch { AddArtifactError(run, "Bounded sanitized failure logs could not be captured."); }
    }

    private void UpdateRunning(CampaignRun run)
    {
        lock (_sync) _status = MakeStatus(run, CampaignOutcome.Running, null, null, null, run.ArtifactId,
            new(false, false, false));
        TryWriteManifest(run, final: false);
    }

    private CampaignStatus MakeStatus(CampaignRun run, CampaignOutcome outcome, string? checkpoint,
        string? reason, DateTimeOffset? ended, string? artifactId, CampaignCleanup cleanup)
    {
        int accepted = run.Accepted;
        int next = accepted % Math.Max(1, run.Definition.OrderedRooms.Count - 1);
        bool routeDone = run.Definition.Kind != CampaignKind.Route || accepted >= run.Definition.RequiredTransitions;
        return new(StatusSchema, run.RunId, run.Definition.Id, run.Definition.Version, run.Definition.EvidenceVersion,
            run.Preflight.BuildVersion, run.Preflight.State.Runtime.ProcessId, TargetMod, run.Preflight.ModVersion, run.Preflight.Diagnostics.SessionId,
            run.Preflight.Diagnostics.Generation, run.StartedUtc, _clock.UtcNow, ended,
            Math.Max(0, (_clock.Elapsed - run.StartElapsed).TotalSeconds), outcome.ToString(), checkpoint,
            reason, new(accepted, run.Definition.RequiredTransitions,
                routeDone ? null : run.Definition.OrderedRooms[next], routeDone ? null : run.Definition.OrderedRooms[next + 1],
                run.LastRoom, (int)Math.Min(int.MaxValue, Math.Max(0, (_clock.Elapsed - run.StartElapsed).TotalSeconds)),
                run.Definition.RequiredSeconds, run.SamplesCompleted, run.Definition.SampleCount),
            run.LastStateFrame, run.LastDiagnosticFrame, cleanup, artifactId);
    }

    private void TryWriteManifest(CampaignRun run, bool final)
    {
        try
        {
            CampaignStatus status;
            lock (_sync) status = _status;
            WriteManifest(run, status, final);
        }
        catch { AddArtifactError(run, "The periodic campaign manifest could not be written."); }
    }

    private void WriteManifest(CampaignRun run, CampaignStatus status, bool final)
    {
        var manifest = new CampaignManifest(ManifestSchema, run.RunId, CampaignCatalog.Schema, status,
            AutomationProtocol.Version, run.Definition.Kind == CampaignKind.Route ? RouteAggregateExecutor.ArtifactSchema : null,
            run.Summaries.ToArray(), run.Screenshots.ToArray(), run.FailureArtifacts.ToArray(),
            run.ArtifactErrors.Take(8).ToArray(), final);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, AutomationProtocol.Json);
        if (bytes.Length > 256 * 1024) throw new InvalidDataException();
        _writer.WriteAtomic(Path.Combine(run.Directory, "manifest.json"), bytes);
    }

    private void EnsureDeadline(CampaignRun run)
    {
        if ((_clock.Elapsed - run.StartElapsed).TotalSeconds > run.Definition.DeadlineSeconds)
            throw new CampaignFailure("deadline", "The campaign exceeded its bounded deadline.");
    }

    private string CreateRunDirectory(string runId)
    {
        bool rootCreated = !Directory.Exists(_root);
        CreateDirectory(_root);
        RejectLink(_root);
        if (rootCreated) MakeDirectoryPrivate(_root);
        string campaigns = DirectChild(_root, "campaigns");
        CreateDirectory(campaigns);
        RejectLink(campaigns);
        MakeDirectoryPrivate(campaigns);
        string directory = DirectChild(campaigns, runId);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("Campaign artifact already exists.");
        CreateDirectory(directory);
        RejectLink(directory);
        MakeDirectoryPrivate(directory);
        return directory;
    }

    private static string ResolveRoot()
    {
        string? configured = Environment.GetEnvironmentVariable("SYMPHONYRECOMP_SCENARIO_ARTIFACTS");
        return string.IsNullOrWhiteSpace(configured) ? Path.Combine(Environment.CurrentDirectory, "artifacts", "scenarios") : configured;
    }
    private static string DefaultId() => $"campaign-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()}";
    private static void ValidateId(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
            throw new InvalidOperationException("Generated campaign identifier is invalid.");
    }
    private static string DirectChild(string root, string name)
    {
        string path = Path.GetFullPath(Path.Combine(root, name));
        if (!string.Equals(Path.GetDirectoryName(path), root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Campaign artifact child is invalid.");
        return path;
    }
    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Campaign artifacts cannot use symbolic links.");
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool IsNeutral(InputTelemetryDto? input) => input is not null &&
        input.AutomationMask1 == 0 && input.AutomationMask2 == 0 &&
        input.AutomationFrames1 == 0 && input.AutomationFrames2 == 0;
    internal static void MakeFilePrivate(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    private static void MakeDirectoryPrivate(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private static void AddArtifactError(CampaignRun run, string value)
    {
        if (run.ArtifactErrors.Count < 8) run.ArtifactErrors.Add(Bound(value, 256));
    }
    private static string Bound(string value, int maximum) => new(value.Where(character => character is >= ' ' and <= '~').Take(maximum).ToArray());
    private static CampaignStatus EmptyStatus() => new(StatusSchema, null, null, null, null, null, null, null, null,
        null, null, null, null, null, 0, "Idle", null, null, new(0, 0, null, null, null, 0, 0, 0, 0),
        null, null, new(false, false, false), null);

    internal sealed class CampaignFailure(string checkpoint, string message) : Exception(message)
    { public string Checkpoint { get; } = checkpoint; }

    private sealed class CampaignRun(string runId, CampaignDefinition definition, CampaignPreflight preflight,
        string directory, IDisposable lease, CancellationTokenSource cancellation, TimeSpan startElapsed,
        DateTimeOffset startedUtc)
    {
        public string RunId { get; } = runId;
        public string ArtifactId => RunId;
        public CampaignDefinition Definition { get; } = definition;
        public CampaignPreflight Preflight { get; } = preflight;
        public string Directory { get; } = directory;
        public IDisposable Lease { get; } = lease;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TimeSpan StartElapsed { get; } = startElapsed;
        public DateTimeOffset StartedUtc { get; } = startedUtc;
        public Task Task { get; set; } = Task.CompletedTask;
        public int Accepted { get; set; }
        public int SamplesCompleted { get; set; }
        public int? LastRoom { get; set; } = preflight.State.Game.Room;
        public long? LastStateFrame { get; set; } = preflight.State.Runtime.Frame;
        public long? LastDiagnosticFrame { get; set; } = preflight.Diagnostics.Frame;
        public List<CampaignSampleSummary> Summaries { get; } = [];
        public List<CampaignScreenshotSummary> Screenshots { get; } = [];
        public List<string> FailureArtifacts { get; } = [];
        public List<string> ArtifactErrors { get; } = [];
        public long LastAttackAllocations { get; set; } = preflight.Snapshot.AttackAllocations;
        public long LastAttackCleanups { get; set; } = preflight.Snapshot.AttackCleanups;
        public long LastAttackLifecycleCancellations { get; set; } = preflight.Snapshot.AttackLifecycleCancellations;
        public long? AttackActiveSinceFrame { get; set; }
    }

    private sealed record CampaignPreflight(CombinedTelemetryDto State, ModDiagnosticsDto Diagnostics,
        DiagnosticSnapshot Snapshot, string ModVersion, string BuildVersion);
}

public sealed record CampaignSampleSummary(string Label, long StateFrame, long DiagnosticFrame, int Room,
    string StateSha256, string DiagnosticsSha256, long TransitionCompleted, long TransitionPassed,
    long ReconstructionSuccesses, long PostTransitionCommandedPixels);
public sealed record CampaignScreenshotSummary(string FileName, long Frame, string Sha256, int ByteLength);
public sealed record CampaignManifest(string Schema, string ArtifactId, string CampaignSchema, CampaignStatus Campaign,
    string ProtocolVersion, string? ScenarioSchema, CampaignSampleSummary[] Samples,
    CampaignScreenshotSummary[] Screenshots, string[] FailureArtifacts, string[] ArtifactErrors, bool Finalized);

internal sealed record DiagnosticSnapshot(string Schema, string KeyboardField, bool Fatal, string ErrorCode,
    long TransitionPassed, long TransitionCompleted, long ReconstructionSuccesses, long ReconstructionFailures,
    bool TransitionPending, bool AwaitingPostTransitionMovement, long PostTransitionCommandedPixels,
    bool PostTransitionMoved, long PostTransitionAbandonments, long TransitionReconstructionFailures,
    long AttackQuarantineSlot, bool AttackCleanupPending, long AttackFailures, long AttackTimingFailures,
    long AttackEquipmentRestoreFailures, long AttackMarkerCount, long AttackOrphanMarkerCount,
    long AttackAllocations, long AttackCleanups, long AttackLifecycleCancellations,
    long AttackExactOwnedLifetimeCurrent, long AttackExactOwnedLifetimeMaximum,
    long ContactGuardFailures, long CollisionRestoreFailures, long VisualRestoreFailures,
    long HealthInvariantFailures, bool DropTrackerFaulted)
{
    public static DiagnosticSnapshot Parse(ModDiagnosticsDto value)
    {
        try
        {
            P2D4Envelope envelope = P2D4EnvelopeParser.Parse(value, "coop-feasibility");
            IReadOnlyDictionary<string, string> fields = envelope.Fields;
            IReadOnlyDictionary<string, P2D4Metric> metrics = envelope.Metrics;
            return new("p2d4/2", fields["K"],
                Bool(metrics, "fatal"), Text(metrics, "errorCode"),
                Long(metrics, "transitionPassed"), Long(metrics, "transitionCompleted"),
                Long(metrics, "reconstructionSuccesses"), Long(metrics, "reconstructionFailures"),
                Bool(metrics, "transitionPending"), Bool(metrics, "awaitingPostTransitionMovement"),
                Long(metrics, "postTransitionCommandedPixels"), Bool(metrics, "postTransitionMoved"),
                Long(metrics, "postTransitionAbandonments"), Long(metrics, "transitionReconstructionFailures"),
                Long(metrics, "attackQuarantineSlot"), Bool(metrics, "attackCleanupPending"),
                Long(metrics, "attackFailures"), Long(metrics, "attackTimingFailures"),
                Long(metrics, "attackEquipmentRestoreFailures"), Long(metrics, "attackMarkerCount"),
                Long(metrics, "attackOrphanMarkerCount"), Long(metrics, "attackAllocations"),
                Long(metrics, "attackCleanups"), Long(metrics, "attackLifecycleCancellations"),
                Long(metrics, "attackExactOwnedLifetimeCurrent"), Long(metrics, "attackExactOwnedLifetimeMaximum"),
                Long(metrics, "contactGuardFailures"),
                Long(metrics, "collisionRestoreFailures"), Long(metrics, "visualRestoreFailures"),
                Long(metrics, "healthInvariantFailures"), Bool(metrics, "dropTrackerFaulted"));
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or InvalidDataException)
        { throw new InvalidDataException("Campaign diagnostics did not match p2d4/2.", exception); }
    }
    private static long Long(IReadOnlyDictionary<string, P2D4Metric> root, string name) =>
        root[name].Integer ?? throw new InvalidDataException();
    private static bool Bool(IReadOnlyDictionary<string, P2D4Metric> root, string name) =>
        root[name].Boolean ?? throw new InvalidDataException();
    private static string Text(IReadOnlyDictionary<string, P2D4Metric> root, string name) =>
        root[name].String ?? throw new InvalidDataException();
}
