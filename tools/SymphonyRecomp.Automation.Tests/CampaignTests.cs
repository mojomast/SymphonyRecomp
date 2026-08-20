using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using ModelContextProtocol;
using SymphonyRecomp.Automation.Contracts;
using SymphonyRecomp.Mcp;
using SymphonyRecomp.Mcp.Campaigns;
using SymphonyRecomp.Mcp.Scenarios;

namespace SymphonyRecomp.Automation.Tests;

public sealed class CampaignTests
{
    private const string Session = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void CatalogIsExactClosedAndRouteMatchesAggregate()
    {
        var catalog = new CampaignCatalog();
        Assert.Equal(["coop-route-25", "coop-soak-60m"], catalog.Inventory);
        CampaignDefinition route = catalog.Get("coop-route-25");
        Assert.Equal(25, route.RequiredTransitions);
        Assert.Equal(RouteAggregateCatalog.No0MarbleGallery25.OrderedRooms, route.OrderedRooms);
        Assert.Equal("coop-route/1|1", RouteAggregateCatalog.ManifestVersion);
        Assert.Equal("34d38244074a0ea351c1374479cafa003bd1a21332e53554fbfba84e30591bac",
            RouteAggregateCatalog.SequenceFingerprint);
        Assert.Throws<McpException>(() => catalog.Get("../route"));
        Assert.Throws<McpException>(() => catalog.Get("unknown"));
    }

    [Fact]
    public async Task UnknownIdRejectsBeforeBridgeOrArtifacts()
    {
        using var temp = new TempDirectory();
        var client = new FakeClient();
        var service = Service(temp.Path, client, new FakeClock());

        await Assert.ThrowsAsync<McpException>(() => service.StartCampaignAsync("unknown", true, default));

        Assert.Equal(0, client.StatusCalls);
        Assert.Empty(Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task RouteConsumesExactlyTwentyFiveTransitionsAndFinalizesBoundedPrivateManifest()
    {
        using var temp = new TempDirectory();
        var client = new FakeClient { Route = true };
        var service = Service(temp.Path, client, new FakeClock());

        CampaignStatus started = await service.StartCampaignAsync("coop-route-25", true, default);
        CampaignStatus result = await WaitTerminal(service);

        Assert.Equal("Passed", result.Outcome);
        Assert.Equal(25, result.Progress.Accepted);
        Assert.True(result.Cleanup.Verified);
        Assert.Equal(started.ArtifactId, result.ArtifactId);
        string manifest = await File.ReadAllTextAsync(Path.Combine(temp.Path, "campaigns", result.ArtifactId!, "manifest.json"));
        Assert.DoesNotContain(temp.Path, manifest);
        Assert.DoesNotContain("disc", manifest, StringComparison.OrdinalIgnoreCase);
        CampaignManifest parsed = JsonSerializer.Deserialize<CampaignManifest>(manifest, AutomationProtocol.Json)!;
        Assert.True(parsed.Finalized);
        Assert.Equal(26, parsed.Samples.Length); // start plus each accepted transition
        Assert.InRange(new FileInfo(Path.Combine(temp.Path, "campaigns", result.ArtifactId!, "manifest.json")).Length, 1, 256 * 1024);
    }

    [Fact]
    public async Task WrongRoutePreservesFirstFailureAndAlwaysUsesFreshCleanupToken()
    {
        using var temp = new TempDirectory();
        var client = new FakeClient { Route = true, WrongTransition = 3 };
        var service = Service(temp.Path, client, new FakeClock());

        await service.StartCampaignAsync("coop-route-25", true, default);
        CampaignStatus result = await WaitTerminal(service);

        Assert.Equal("Failed", result.Outcome);
        Assert.Equal("route", result.FirstFailedCheckpoint);
        Assert.True(result.Cleanup.Attempted);
        Assert.False(client.CleanupToken.IsCancellationRequested);
    }

    [Fact]
    public async Task OneCampaignMaximumAndExplicitCancelReleasesMutationGate()
    {
        using var temp = new TempDirectory();
        var clock = new FakeClock { Block = true };
        var gate = new ScenarioExecutionGate();
        var service = Service(temp.Path, new FakeClient(), clock, gate);
        await service.StartCampaignAsync("coop-soak-60m", true, default);

        await Assert.ThrowsAsync<McpException>(() => service.StartCampaignAsync("coop-soak-60m", true, default));
        Assert.Throws<McpException>(() => gate.TryEnterMutation());
        CampaignStatus cancelled = await service.CancelAsync(true, default);

        Assert.Equal("Cancelled", cancelled.Outcome);
        using IDisposable mutation = gate.TryEnterMutation();
    }

    [Fact]
    public async Task SoakUsesMonotonicClockAndCapturesThirteenSamplesWithoutRealWaiting()
    {
        using var temp = new TempDirectory();
        var service = Service(temp.Path, new FakeClient(), new FakeClock { Advance = TimeSpan.FromMinutes(5) });

        await service.StartCampaignAsync("coop-soak-60m", true, default);
        CampaignStatus result = await WaitTerminal(service);

        Assert.Equal("Passed", result.Outcome);
        Assert.Equal(13, result.Progress.SamplesCompleted);
        Assert.Equal(3600, result.Progress.RequiredSeconds);
        Assert.InRange(result.ElapsedSeconds, 3600, 3900);
    }

    [Fact]
    public async Task SoakCrossingHourCapturesFinalSampleExactlyOnce()
    {
        using var temp = new TempDirectory();
        var clock = new FakeClock { Advances = [TimeSpan.FromSeconds(3599), TimeSpan.FromSeconds(2)] };
        var service = Service(temp.Path, new FakeClient(), clock);

        await service.StartCampaignAsync("coop-soak-60m", true, default);
        CampaignStatus result = await WaitTerminal(service);

        Assert.Equal("Passed", result.Outcome);
        string manifest = await File.ReadAllTextAsync(Path.Combine(temp.Path, "campaigns", result.ArtifactId!, "manifest.json"));
        CampaignManifest parsed = JsonSerializer.Deserialize<CampaignManifest>(manifest, AutomationProtocol.Json)!;
        Assert.Equal(13, parsed.Samples.Length);
        Assert.Single(parsed.Samples, sample => sample.Label == "minute-60");
    }

    [Fact]
    public async Task InvalidFinalMinuteSixtySamplePreventsPass()
    {
        using var temp = new TempDirectory();
        var client = new FakeClient { UnsafeTelemetryAtCall = 6 };
        var clock = new FakeClock { Advances = [TimeSpan.FromSeconds(3599), TimeSpan.FromSeconds(2)] };
        var service = Service(temp.Path, client, clock);

        await service.StartCampaignAsync("coop-soak-60m", true, default);
        CampaignStatus result = await WaitTerminal(service);

        Assert.Equal("Failed", result.Outcome);
        Assert.Equal("game-state", result.FirstFailedCheckpoint);
        Assert.Equal(12, result.Progress.SamplesCompleted);
    }

    [Fact]
    public async Task SoakFailsBoundedlyOnNoFrameProgress()
    {
        using var temp = new TempDirectory();
        var service = Service(temp.Path, new FakeClient { FreezeFrames = true },
            new FakeClock { Advance = TimeSpan.FromSeconds(11) });
        await service.StartCampaignAsync("coop-soak-60m", true, default);

        CampaignStatus result = await WaitTerminal(service);

        Assert.Equal("Failed", result.Outcome);
        Assert.Equal("frame-progress", result.FirstFailedCheckpoint);
    }

    [Fact]
    public async Task IdentityChangeAndMetricFailureFailClosed()
    {
        using var identityTemp = new TempDirectory();
        var identity = Service(identityTemp.Path, new FakeClient { Route = true, ChangeSessionAtTransition = 2 }, new FakeClock());
        await identity.StartCampaignAsync("coop-route-25", true, default);
        Assert.Equal("identity", (await WaitTerminal(identity)).FirstFailedCheckpoint);

        using var metricTemp = new TempDirectory();
        var metric = Service(metricTemp.Path, new FakeClient { Route = true, FailMetricAtTransition = 2 }, new FakeClock());
        await metric.StartCampaignAsync("coop-route-25", true, default);
        Assert.Equal("safety", (await WaitTerminal(metric)).FirstFailedCheckpoint);
    }

    [Fact]
    public async Task ShutdownCancelsObserverAndReleasesGate()
    {
        using var temp = new TempDirectory();
        var gate = new ScenarioExecutionGate();
        var service = Service(temp.Path, new FakeClient(), new FakeClock { Block = true }, gate);
        await service.StartCampaignAsync("coop-soak-60m", true, default);

        await service.StopAsync(default);

        Assert.Equal("Cancelled", service.GetStatus().Outcome);
        using IDisposable mutation = gate.TryEnterMutation();
    }

    [Fact]
    public async Task StartRejectsActiveBridgeOrTelemetryWithoutArtifactsOrLease()
    {
        using var bridgeTemp = new TempDirectory();
        var bridgeGate = new ScenarioExecutionGate();
        var bridge = Service(bridgeTemp.Path, new FakeClient { BridgeInputActive = true },
            new FakeClock(), bridgeGate);
        await Assert.ThrowsAsync<McpException>(() => bridge.StartCampaignAsync("coop-soak-60m", true, default));
        Assert.Equal("Idle", bridge.GetStatus().Outcome);
        Assert.Empty(Directory.GetFileSystemEntries(bridgeTemp.Path));
        using (bridgeGate.TryEnterMutation()) { }

        using var maskTemp = new TempDirectory();
        var maskGate = new ScenarioExecutionGate();
        var masks = Service(maskTemp.Path, new FakeClient { TelemetryMaskActive = true },
            new FakeClock(), maskGate);
        await Assert.ThrowsAsync<McpException>(() => masks.StartCampaignAsync("coop-soak-60m", true, default));
        Assert.Equal("Idle", masks.GetStatus().Outcome);
        Assert.Empty(Directory.GetFileSystemEntries(maskTemp.Path));
        using (maskGate.TryEnterMutation()) { }

        using var framesTemp = new TempDirectory();
        var frames = Service(framesTemp.Path, new FakeClient { TelemetryFramesActive = true },
            new FakeClock());
        await Assert.ThrowsAsync<McpException>(() => frames.StartCampaignAsync("coop-soak-60m", true, default));
        Assert.Equal("Idle", frames.GetStatus().Outcome);
        Assert.Empty(Directory.GetFileSystemEntries(framesTemp.Path));
    }

    [Fact]
    public async Task InputInstalledBetweenChecksCannotRaceCampaignOwnership()
    {
        using var temp = new TempDirectory();
        var gate = new ScenarioExecutionGate();
        var service = Service(temp.Path, new FakeClient { ActivateInputAfterTelemetryCall = 1 },
            new FakeClock(), gate);

        await Assert.ThrowsAsync<McpException>(() => service.StartCampaignAsync("coop-soak-60m", true, default));

        Assert.Equal("Idle", service.GetStatus().Outcome);
        Assert.Empty(Directory.GetFileSystemEntries(temp.Path));
        using IDisposable mutation = gate.TryEnterMutation();
    }

    [Fact]
    public async Task SoakAllowsBoundedTransientAttackButRejectsStuckMarker()
    {
        using var transientTemp = new TempDirectory();
        var transient = Service(transientTemp.Path,
            new FakeClient { TransientAttackCaptures = 2 },
            new FakeClock { Advance = TimeSpan.FromMinutes(5) });
        await transient.StartCampaignAsync("coop-soak-60m", true, default);
        Assert.Equal("Passed", (await WaitTerminal(transient)).Outcome);

        using var stuckTemp = new TempDirectory();
        var stuck = Service(stuckTemp.Path,
            new FakeClient { StuckAttack = true, DiagnosticFrameAdvance = 50 },
            new FakeClock { Advance = TimeSpan.FromSeconds(1) });
        await stuck.StartCampaignAsync("coop-soak-60m", true, default);
        CampaignStatus result = await WaitTerminal(stuck);
        Assert.Equal("Failed", result.Outcome);
        Assert.Equal("attack-lifecycle", result.FirstFailedCheckpoint);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("missingAttackLifetime")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("type")]
    [InlineData("frame")]
    public async Task CampaignRejectsIncompleteUnknownMistypedOrFrameMismatchedV2(string malformed)
    {
        using var temp = new TempDirectory();
        var service = Service(temp.Path, new FakeClient { MalformedMetric = malformed }, new FakeClock());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.StartCampaignAsync("coop-soak-60m", true, default));
        Assert.Equal("Idle", service.GetStatus().Outcome);
    }

    [Fact]
    public async Task SoakRejectsAttackCounterRegression()
    {
        using var temp = new TempDirectory();
        var service = Service(temp.Path, new FakeClient { RegressAttackCounter = true },
            new FakeClock { Advance = TimeSpan.FromSeconds(1) });
        await service.StartCampaignAsync("coop-soak-60m", true, default);
        CampaignStatus result = await WaitTerminal(service);
        Assert.Equal("Failed", result.Outcome);
        Assert.Equal("attack-lifecycle", result.FirstFailedCheckpoint);
    }

    [Fact]
    public async Task SoakRejectsOverlongExactAttackCompletedBetweenPolls()
    {
        using var temp = new TempDirectory();
        var service = Service(temp.Path, new FakeClient { OverlongAttackBetweenPolls = true },
            new FakeClock { Advance = TimeSpan.FromMinutes(5) });
        await service.StartCampaignAsync("coop-soak-60m", true, default);

        CampaignStatus result = await WaitTerminal(service);
        Assert.Equal("Failed", result.Outcome);
        Assert.Equal("attack-lifecycle", result.FirstFailedCheckpoint);
    }

    [Fact]
    public async Task FinalWriterFailureCannotBecomePassedAndAttemptsFailureManifest()
    {
        using var temp = new TempDirectory();
        var writer = new FailPassedFinalManifestWriter();
        var service = Service(temp.Path, new FakeClient(),
            new FakeClock { Advance = TimeSpan.FromMinutes(5) }, writer: writer);
        await service.StartCampaignAsync("coop-soak-60m", true, default);

        CampaignStatus result = await WaitTerminal(service);

        Assert.Equal("Failed", result.Outcome);
        Assert.Equal("artifacts", result.FirstFailedCheckpoint);
        Assert.True(writer.PassedFinalRejected);
        Assert.True(writer.FailureFinalWritten);
    }

    [Fact]
    public async Task UnixCampaignArtifactsArePrivate()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temp = new TempDirectory();
        var service = Service(temp.Path, new FakeClient(),
            new FakeClock { Advance = TimeSpan.FromMinutes(5) });
        await service.StartCampaignAsync("coop-soak-60m", true, default);
        CampaignStatus result = await WaitTerminal(service);
        string campaigns = Path.Combine(temp.Path, "campaigns");
        string run = Path.Combine(campaigns, result.ArtifactId!);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(campaigns));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(run));
        foreach (string file in Directory.GetFiles(run))
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
    }

    [Fact]
    public async Task DisposalAwaitsCancellationOfBlockedClientAndLeaseRelease()
    {
        using var temp = new TempDirectory();
        var gate = new ScenarioExecutionGate();
        var client = new FakeClient { BlockTelemetryAfterPreflight = true };
        var service = Service(temp.Path, client, new FakeClock(), gate);
        await service.StartCampaignAsync("coop-soak-60m", true, default);
        await client.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await service.DisposeAsync();

        Assert.True(client.BlockCompleted);
        Assert.Equal("Cancelled", service.GetStatus().Outcome);
        using IDisposable mutation = gate.TryEnterMutation();
    }

    private static CampaignService Service(string root, FakeClient client, FakeClock clock,
        ScenarioExecutionGate? gate = null, ICampaignArtifactWriter? writer = null) =>
        new(client, new CampaignCatalog(), gate ?? new(), clock,
            root, () => $"campaign-test-{Guid.NewGuid():N}", writer);

    private static async Task<CampaignStatus> WaitTerminal(CampaignService service)
    {
        for (int attempt = 0; attempt < 500; attempt++)
        {
            CampaignStatus value = service.GetStatus();
            if (value.Outcome != "Running") return value;
            await Task.Delay(1);
        }
        throw new TimeoutException();
    }

    private sealed class FakeClock : ICampaignClock
    {
        private TimeSpan _elapsed;
        public bool Block { get; init; }
        public TimeSpan? Advance { get; init; }
        public TimeSpan[]? Advances { get; init; }
        private int _advanceIndex;
        public DateTimeOffset UtcNow => new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero) + _elapsed;
        public TimeSpan Elapsed => _elapsed;
        public async Task DelayAsync(TimeSpan delay, CancellationToken token)
        {
            if (Block) { await Task.Delay(Timeout.InfiniteTimeSpan, token); return; }
            _elapsed += Advances is { Length: > 0 }
                ? Advances[Math.Min(_advanceIndex++, Advances.Length - 1)]
                : Advance ?? delay;
            await Task.Yield();
            token.ThrowIfCancellationRequested();
        }
    }

    private sealed class FakeClient : IScenarioAutomationClient
    {
        private static readonly int[] Rooms = [9, 10, 5, 6, 5, 10, 9, 19, 11, 19, 9];
        private long _frame = 100;
        private int _telemetryCalls;
        private int _allTelemetryCalls;
        private int _transitions;
        private bool _preflightCaptured;
        private int _diagnosticCalls;
        public bool Route { get; init; }
        public int WrongTransition { get; init; } = -1;
        public int ChangeSessionAtTransition { get; init; } = -1;
        public int FailMetricAtTransition { get; init; } = -1;
        public bool FreezeFrames { get; init; }
        public bool BridgeInputActive { get; init; }
        public bool TelemetryInputActive { get; set; }
        public bool TelemetryMaskActive { get; init; }
        public bool TelemetryFramesActive { get; init; }
        public int ActivateInputAfterTelemetryCall { get; init; } = -1;
        public int TransientAttackCaptures { get; init; }
        public bool StuckAttack { get; init; }
        public int DiagnosticFrameAdvance { get; init; }
        public string? MalformedMetric { get; init; }
        public bool RegressAttackCounter { get; init; }
        public bool OverlongAttackBetweenPolls { get; init; }
        public int UnsafeTelemetryAtCall { get; init; } = -1;
        public bool BlockTelemetryAfterPreflight { get; init; }
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockCompleted { get; private set; }
        public int StatusCalls { get; private set; }
        public CancellationToken CleanupToken { get; private set; }

        public Task<BridgeStatusDto> GetBridgeStatusAsync(CancellationToken token)
        {
            StatusCalls++;
            return Task.FromResult(new BridgeStatusDto("1.2", true, _frame, 42, "running", "Play", "MarbleGallery", 0, BridgeInputActive));
        }
        public async Task<CombinedTelemetryDto> GetTelemetryAsync(CancellationToken token)
        {
            if (BlockTelemetryAfterPreflight && _preflightCaptured)
            {
                Blocked.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { BlockCompleted = true; }
            }
            _allTelemetryCalls++;
            if (!FreezeFrames || _allTelemetryCalls == 1) _frame++;
            int room = 9;
            bool active = TelemetryInputActive;
            if (_allTelemetryCalls == ActivateInputAfterTelemetryCall) TelemetryInputActive = true;
            if (Route && _preflightCaptured)
            {
                _transitions++;
                int index = _transitions % (Rooms.Length - 1);
                room = WrongTransition == _transitions ? 77 : Rooms[index];
            }
            else if (Route) _telemetryCalls++;
            var runtime = new RuntimeTelemetryDto(_frame, 42, 1000, 0, [], 0, null, null, null);
            var input = new InputTelemetryDto(0, 0, 0, 0,
                active || TelemetryMaskActive ? (ushort)1 : (ushort)0, 0,
                active || TelemetryFramesActive ? 1 : 0, 0);
            bool unsafeSample = _allTelemetryCalls == UnsafeTelemetryAtCall;
            var game = new GameTelemetryDto(true, "Play", "Play", 1, 1, "Alucard", "MarbleGallery",
                0, room, 0, 0, unsafeSample, false, false, 0, 0, null, input, null, null);
            return new CombinedTelemetryDto(runtime, game);
        }
        public Task<ModDiagnosticsDto> CaptureModDiagnosticsAsync(ModDiagnosticsCaptureRequest request, CancellationToken token)
        {
            int transitions = Math.Max(0, _transitions);
            _diagnosticCalls++;
            _preflightCaptured = true;
            if (DiagnosticFrameAdvance != 0) _frame += DiagnosticFrameAdvance;
            string session = ChangeSessionAtTransition == _transitions ? new string('f', 32) : Session;
            bool transient = _diagnosticCalls > 1 &&
                (StuckAttack || _diagnosticCalls <= TransientAttackCaptures + 1);
            var metrics = ScenarioMetricContract.Types.ToDictionary(pair => pair.Key, pair => pair.Value switch
            {
                MetricScalarType.Integer => (object)MetricInteger(pair.Key, transitions, transient),
                MetricScalarType.Boolean => MetricBoolean(pair.Key, transient),
                MetricScalarType.String => "0",
                _ => throw new InvalidOperationException(),
            }, StringComparer.Ordinal);
            if (MalformedMetric == "missing") metrics.Remove("transitionPassed");
            if (MalformedMetric == "missingAttackLifetime") metrics.Remove("attackExactOwnedLifetimeMaximum");
            if (MalformedMetric == "unknown") metrics.Add("unknownMetric", 0L);
            if (MalformedMetric == "type") metrics["transitionPassed"] = false;
            JsonElement payload = JsonSerializer.SerializeToElement(new
            {
                schema = "p2d4/2", modVersion = "0.4.0", sessionId = session, generation = 0,
                modFrame = _frame, automationFrame = MalformedMetric == "frame" ? _frame + 1 : _frame,
                legacy = "P2D4 test",
                fields = P2D4Fields(),
                metrics
            });
            if (MalformedMetric == "duplicate")
            {
                string duplicated = payload.GetRawText().Replace("\"transitionPassed\":0",
                    "\"transitionPassed\":0,\"transitionPassed\":0", StringComparison.Ordinal);
                using JsonDocument document = JsonDocument.Parse(duplicated);
                payload = document.RootElement.Clone();
            }
            return Task.FromResult(new ModDiagnosticsDto(request.Id, _frame, session, 0, payload));
        }
        private static Dictionary<string, string> P2D4Fields()
        {
            string[] names = ["VER", "H", "I", "K", "M", "R", "N", "B", "C", "T", "S", "G", "Q", "A", "E",
                "D", "VIS", "J", "X", "EN", "AW", "HU", "HP"];
            return names.ToDictionary(name => name,
                name => name == "VER" ? "0.4.0" : name == "K" ? "-" : "P", StringComparer.Ordinal);
        }
        private long MetricInteger(string name, int transitions, bool transient) => name switch
        {
            "transitionPassed" or "transitionCompleted" or "reconstructionSuccesses" => transitions,
            "postTransitionCommandedPixels" => transitions * 8L,
            "attackQuarantineSlot" => -1,
            "attackMarkerCount" => transient ? 1 : 0,
            "attackExactOwnedLifetimeMaximum" when OverlongAttackBetweenPolls && _diagnosticCalls > 1 => 49,
            "attackAllocations" when RegressAttackCounter && _diagnosticCalls == 2 => 1,
            "attackAllocations" when RegressAttackCounter && _diagnosticCalls > 2 => 0,
            "attackFailures" when FailMetricAtTransition == _transitions => 1,
            "healthHp" => 100,
            _ => 0,
        };
        private static bool MetricBoolean(string name, bool transient) => name switch
        {
            "postTransitionMoved" => true,
            "attackCleanupPending" => transient,
            _ => false,
        };
        public Task<OperationResultDto> ClearInputAsync(CancellationToken token)
        {
            CleanupToken = token;
            return Task.FromResult(new OperationResultDto(true, _frame, "cleared"));
        }
        public Task<ModTelemetryDto[]> ListModsAsync(CancellationToken token) => Task.FromResult(new[]
        { new ModTelemetryDto("coop-feasibility", "Co-op", "0.4.0", "test", "test", [], true, true, 1, true, null) });
        public Task<ScenarioBuildIdentity> GetBuildIdentityAsync(CancellationToken token) => Task.FromResult(
            new ScenarioBuildIdentity("game.exe", new string('a', 64), "test-build", "1.2", 42, "running"));
        public Task<ScreenshotDto> CaptureScreenshotAsync(CancellationToken token)
        {
            byte[] png = new byte[24];
            new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0);
            "IHDR"u8.CopyTo(png.AsSpan(12));
            BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16, 4), 2);
            BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20, 4), 1);
            return Task.FromResult(new ScreenshotDto(_frame, 2, 1, "image/png", "display",
                Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant(), Convert.ToBase64String(png)));
        }
        public Task<ModDiagnosticsResetDto> ResetModDiagnosticsAsync(ModDiagnosticsResetRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<InputOperationDto> RunInputAsync(InputTimelineRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<InputBatchOperationDto> RunInputBatchAsync(InputBatchRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<EntityListDto> ListEntitiesAsync(int maximum, CancellationToken token) => throw new NotSupportedException();
        public Task<LogsResult> GetLogsAsync(int maximum, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class FailPassedFinalManifestWriter : ICampaignArtifactWriter
    {
        private readonly CampaignArtifactWriter _inner = new();
        public bool PassedFinalRejected { get; private set; }
        public bool FailureFinalWritten { get; private set; }
        public void WriteAtomic(string path, ReadOnlySpan<byte> bytes)
        {
            if (Path.GetFileName(path) == "manifest.json")
            {
                using JsonDocument document = JsonDocument.Parse(bytes.ToArray());
                JsonElement root = document.RootElement;
                if (root.GetProperty("finalized").GetBoolean())
                {
                    string outcome = root.GetProperty("campaign").GetProperty("outcome").GetString()!;
                    if (outcome == "Passed")
                    {
                        PassedFinalRejected = true;
                        throw new IOException("injected final write failure");
                    }
                    if (outcome == "Failed") FailureFinalWritten = true;
                }
            }
            _inner.WriteAtomic(path, bytes);
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sotn-campaign-{Guid.NewGuid():N}");
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
