using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SymphonyRecomp.Automation.Contracts;
using SymphonyRecomp.Mcp;
using SymphonyRecomp.Mcp.Scenarios;

namespace SymphonyRecomp.Automation.Tests;

public sealed class ScenarioArtifactTests
{
    private const string Session = "0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public async Task SuccessfulPolicyWritesExactBoundedBundleAndIdentity()
    {
        using var temp = new TempDirectory();
        var fake = new ArtifactClient();
        string source = Source(fail: false, allOnSuccess: true);
        var service = Service(temp.Path, fake, "safe-run");

        ScenarioExecutionResult result = await service.RunAsync(source);

        Assert.Equal("safe-run", result.RunId);
        Assert.Equal("safe-run", result.ArtifactId);
        string run = Path.Combine(temp.Path, result.RunId);
        Assert.Equal(source, await File.ReadAllTextAsync(Path.Combine(run, "scenario.json")));
        Assert.Equal(fake.Png, await File.ReadAllBytesAsync(Path.Combine(run, "screenshot.png")));
        ScenarioArtifactManifest manifest = ReadManifest(run);
        Assert.Equal(ScenarioRunOutcome.Passed.ToString(), manifest.Outcome);
        Assert.Equal(ScenarioParser.Schema, manifest.Scenario.Schema);
        Assert.Equal("artifact-test", manifest.Scenario.Id);
        Assert.Equal("2.3.4", manifest.Scenario.Version);
        Assert.Equal(Hash(source), manifest.Scenario.SourceSha256);
        Assert.Equal("1.2.3+test", manifest.Runtime.McpInformationalVersion);
        Assert.Equal(AutomationProtocol.Version, manifest.Runtime.ProtocolVersion);
        Assert.Equal("game.bin", manifest.Runtime.ExecutableFileName);
        Assert.Equal(new string('a', 64), manifest.Runtime.ExecutableSha256);
        Assert.Equal(321, manifest.Runtime.ProcessId);
        Assert.Equal(new ScenarioModIdentity("coop", "0.4.0"), manifest.Mod);
        Assert.Equal("p2d4/1", manifest.Diagnostics!.Schema);
        Assert.Equal(Session, manifest.Diagnostics.SessionId);
        Assert.True(manifest.Cleanup.Succeeded);
        Assert.True(manifest.Cleanup.Verified);
        Assert.Equal(["state", "diagnostics", "entities", "logs", "screenshot"], manifest.RequestedArtifacts);
        Assert.Equal(manifest.RequestedArtifacts, manifest.CapturedArtifacts);
        Assert.Empty(manifest.ArtifactErrors);

        EntityListDto entities = JsonSerializer.Deserialize<EntityListDto>(
            await File.ReadAllBytesAsync(Path.Combine(run, "entities.json")), AutomationProtocol.Json)!;
        Assert.Equal(256, entities.Entities.Length);
        LogsResult logs = JsonSerializer.Deserialize<LogsResult>(
            await File.ReadAllBytesAsync(Path.Combine(run, "logs.json")), AutomationProtocol.Json)!;
        Assert.Equal(200, logs.BridgeLines.Length);
        Assert.Equal(200, logs.ProcessLines.Length);
        Assert.All(logs.BridgeLines, line => Assert.True(line.Length <= 4096));
        Assert.DoesNotContain("/private/discs", await File.ReadAllTextAsync(Path.Combine(run, "state.json")));
        Assert.Empty(Directory.GetDirectories(temp.Path, ".tmp-*"));
    }

    [Fact]
    public async Task FailureBundleCapturesAllKindsAndPreservesFirstCheckpointAndCleanup()
    {
        using var temp = new TempDirectory();
        var fake = new ArtifactClient { FailCheckpoint = true };

        ScenarioExecutionResult result = await Service(temp.Path, fake, "failed-run")
            .RunAsync(Source(fail: true, allOnSuccess: false));

        ScenarioArtifactManifest manifest = ReadManifest(Path.Combine(temp.Path, result.RunId));
        Assert.Equal(ScenarioRunOutcome.Failed.ToString(), manifest.Outcome);
        Assert.Equal("start", manifest.FirstFailedCheckpoint);
        Assert.Contains("terminal diagnostic failure", manifest.PrimaryError);
        Assert.Equal(["state", "diagnostics", "entities", "logs", "screenshot"], manifest.CapturedArtifacts);
        Assert.True(manifest.Cleanup.Attempted);
        Assert.True(manifest.Cleanup.Succeeded);
        Assert.True(manifest.Cleanup.Verified);
        Assert.Equal(1, fake.ClearCalls);
    }

    [Fact]
    public async Task InvalidScreenshotIsRecordedWithoutReplacingPrimaryFailure()
    {
        using var temp = new TempDirectory();
        var fake = new ArtifactClient { FailCheckpoint = true, InvalidScreenshot = true };

        ScenarioExecutionResult result = await Service(temp.Path, fake, "invalid-png")
            .RunAsync(Source(fail: true, allOnSuccess: false));

        string run = Path.Combine(temp.Path, result.RunId);
        ScenarioArtifactManifest manifest = ReadManifest(run);
        Assert.Equal("start", manifest.FirstFailedCheckpoint);
        Assert.Contains("terminal diagnostic failure", manifest.PrimaryError);
        Assert.DoesNotContain("screenshot", manifest.CapturedArtifacts);
        Assert.Contains(manifest.ArtifactErrors, error =>
            error == new ScenarioArtifactError("screenshot", "capture",
                "The artifact could not be captured or validated."));
        Assert.False(File.Exists(Path.Combine(run, "screenshot.png")));
    }

    [Fact]
    public async Task CaptureAndWriteFailuresAreBoundedAndDoNotLeakOrReplaceFailure()
    {
        using var temp = new TempDirectory();
        var fake = new ArtifactClient
        {
            FailCheckpoint = true,
            EntityFailure = new IOException("token=topsecret /host/private/game.bin"),
        };
        var writer = new FailingWriter("logs.json", "token=writersecret /host/output");
        ScenarioExecutionService service = Service(temp.Path, fake, "partial-run", writer);

        ScenarioExecutionResult result = await service.RunAsync(Source(fail: true, allOnSuccess: false));

        string manifestText = await File.ReadAllTextAsync(Path.Combine(temp.Path, result.RunId, "manifest.json"));
        ScenarioArtifactManifest manifest = JsonSerializer.Deserialize<ScenarioArtifactManifest>(
            manifestText, AutomationProtocol.Json)!;
        Assert.Equal("start", manifest.FirstFailedCheckpoint);
        Assert.Contains("terminal diagnostic failure", manifest.PrimaryError);
        Assert.Contains(manifest.ArtifactErrors, error => error.Artifact == "entities" && error.Stage == "capture");
        Assert.Contains(manifest.ArtifactErrors, error => error.Artifact == "logs" && error.Stage == "write");
        Assert.All(manifest.ArtifactErrors, error => Assert.True(error.Message.Length <= 256));
        Assert.DoesNotContain("topsecret", manifestText);
        Assert.DoesNotContain("writersecret", manifestText);
        Assert.DoesNotContain("/host/", manifestText);
    }

    [Fact]
    public async Task GeneratedNamesRejectTraversalAndExistingRunsAreNeverOverwritten()
    {
        using var temp = new TempDirectory();
        string source = Source(fail: false, allOnSuccess: false);
        var fake = new ArtifactClient();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(temp.Path, fake, "../escape").RunAsync(source));
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(temp.Path)!, "escape")));

        ScenarioExecutionService service = Service(temp.Path, fake, "same-run");
        await service.RunAsync(source);
        string manifest = await File.ReadAllTextAsync(Path.Combine(temp.Path, "same-run", "manifest.json"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(source));
        Assert.Equal(manifest, await File.ReadAllTextAsync(Path.Combine(temp.Path, "same-run", "manifest.json")));
    }

    [Fact]
    public async Task PreCancelledCallDoesNotCaptureIdentityOrRunScenario()
    {
        using var temp = new TempDirectory();
        var fake = new ArtifactClient();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => Service(temp.Path, fake, "cancelled-before")
            .RunAsync(Source(fail: false, allOnSuccess: false), cancellation.Token));

        Assert.Equal(0, fake.BuildIdentityCalls);
        Assert.Equal(0, fake.StatusCalls);
    }

    [Fact]
    public async Task CancelledRunCapturesEvidenceUnderOneIndependentBudget()
    {
        using var temp = new TempDirectory();
        using var cancellation = new CancellationTokenSource();
        var fake = new ArtifactClient { CancelOnStatus = cancellation.Cancel };
        var writer = new RecordingWriter();

        ScenarioExecutionResult result = await Service(temp.Path, fake, "cancelled-evidence", writer)
            .RunAsync(Source(fail: true, allOnSuccess: false), cancellation.Token);

        Assert.Equal(ScenarioRunOutcome.Cancelled, result.Run.Outcome);
        Assert.Equal(5, fake.EvidenceTokens.Count);
        Assert.All(fake.EvidenceTokens, token =>
        {
            Assert.True(token.CanBeCanceled);
            Assert.False(token.IsCancellationRequested);
            Assert.Equal(fake.EvidenceTokens[0], token);
        });
        CancellationToken[] evidenceWrites = writer.Writes
            .Where(write => write.FileName is not ("scenario.json" or "manifest.json"))
            .Select(write => write.Token).ToArray();
        Assert.Equal(5, evidenceWrites.Length);
        Assert.All(evidenceWrites, token => Assert.Equal(fake.EvidenceTokens[0], token));
    }

    [Fact]
    public async Task PostRunEvidenceCannotHoldExecutionUnbounded()
    {
        using var temp = new TempDirectory();
        var fake = new ArtifactClient { FailCheckpoint = true, HangEntities = true };
        ScenarioExecutionService service = Service(temp.Path, fake, "bounded-evidence",
            postRunEvidenceTimeout: TimeSpan.FromMilliseconds(50));
        var gate = new ScenarioExecutionGate();

        ScenarioExecutionResult result;
        using (gate.TryEnterScenario())
        {
            result = await service.RunAsync(Source(fail: true, allOnSuccess: false))
                .WaitAsync(TimeSpan.FromSeconds(2));
        }

        Assert.Equal(ScenarioRunOutcome.Failed, result.Run.Outcome);
        Assert.Contains(result.Manifest.ArtifactErrors,
            error => error.Artifact == "entities" && error.Stage == "capture");
        using IDisposable mutation = gate.TryEnterMutation();
    }

    private static ScenarioExecutionService Service(string root, ArtifactClient client, string runId,
        IScenarioArtifactFileWriter? writer = null, TimeSpan? postRunEvidenceTimeout = null) =>
        new(client, new ImmediateClock(), root, static () => Now, (_, _) => runId, writer,
            postRunEvidenceTimeout);

    private static ScenarioArtifactManifest ReadManifest(string runDirectory) =>
        JsonSerializer.Deserialize<ScenarioArtifactManifest>(
            File.ReadAllBytes(Path.Combine(runDirectory, "manifest.json")), AutomationProtocol.Json)!;

    private static string Hash(string source) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();

    private static string Source(bool fail, bool allOnSuccess)
    {
        string all = "[\"state\",\"diagnostics\",\"entities\",\"logs\",\"screenshot\"]";
        const string template = """
            {"schema":"sotn-scenario/1","id":"artifact-test","version":"2.3.4","description":"artifact test","modId":"coop","timeoutMs":30000,"start":{"timeoutFrames":20,"predicates":[{"type":"diagnostic","schema":"p2d4/1","field":"M","result":"P"}]},"steps":[{"id":"one","inputs":[{"port":0,"timeline":[{"buttons":[],"frames":1}]}],"checkpoint":{"timeoutFrames":20,"predicates":[{"type":"diagnostic","schema":"p2d4/1","field":"M","result":"P"}]}}],"artifacts":{"onFailure":FAILURE,"onSuccess":SUCCESS}}
            """;
        return template.Replace("FAILURE", fail ? all : "[]", StringComparison.Ordinal)
            .Replace("SUCCESS", allOnSuccess ? all : "[]", StringComparison.Ordinal);
    }

    private sealed class ImmediateClock : IScenarioClock
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class ArtifactClient : IScenarioAutomationClient
    {
        private int _generation;
        private long _frame;
        public bool FailCheckpoint { get; init; }
        public bool InvalidScreenshot { get; init; }
        public Exception? EntityFailure { get; init; }
        public bool HangEntities { get; init; }
        public Action? CancelOnStatus { get; init; }
        public int ClearCalls { get; private set; }
        public int BuildIdentityCalls { get; private set; }
        public int StatusCalls { get; private set; }
        public List<CancellationToken> EvidenceTokens { get; } = [];
        public byte[] Png { get; } = CreatePng();
        private bool _cleared;
        private bool _cleanupTelemetrySeen;

        public Task<BridgeStatusDto> GetBridgeStatusAsync(CancellationToken token)
        {
            StatusCalls++;
            CancelOnStatus?.Invoke();
            if (token.IsCancellationRequested) return Task.FromCanceled<BridgeStatusDto>(token);
            return Task.FromResult(new BridgeStatusDto(AutomationProtocol.Version, true, _frame, 321,
                "running", "Play", "CEN", 0, false));
        }

        public Task<CombinedTelemetryDto> GetTelemetryAsync(CancellationToken token)
        {
            if (_cleared)
            {
                if (_cleanupTelemetrySeen) EvidenceTokens.Add(token);
                else _cleanupTelemetrySeen = true;
            }
            _frame++;
            var runtime = new RuntimeTelemetryDto(_frame, 321, 1000, 0, [], 1, null, null,
                new CdTelemetryDto(true, "/private/discs/legal-game.cue"));
            var input = new InputTelemetryDto(0, 0, 0, 0, 0, 0, 0, 0);
            var game = new GameTelemetryDto(true, "Play", "Play", 2, 3, "Alucard", "CEN",
                0, 0, 0, 0, false, false, false, 0, 0, null, input, null, null);
            return Task.FromResult(new CombinedTelemetryDto(runtime, game));
        }

        public Task<ModDiagnosticsDto> CaptureModDiagnosticsAsync(ModDiagnosticsCaptureRequest request,
            CancellationToken token)
        {
            if (_cleared) EvidenceTokens.Add(token);
            string value = FailCheckpoint && _generation == 0 ? "F:primary" : "P";
            JsonElement payload = JsonSerializer.SerializeToElement(new
            {
                schema = "p2d4/1",
                sessionId = Session,
                generation = _generation,
                automationFrame = _frame,
                fields = new Dictionary<string, string> { ["M"] = value },
            });
            return Task.FromResult(new ModDiagnosticsDto(request.Id, _frame, Session, _generation, payload));
        }

        public Task<ModDiagnosticsResetDto> ResetModDiagnosticsAsync(ModDiagnosticsResetRequest request,
            CancellationToken token)
        {
            _generation++;
            return Task.FromResult(new ModDiagnosticsResetDto(request.Id, _frame, true));
        }

        public Task<InputOperationDto> RunInputAsync(InputTimelineRequest request, CancellationToken token) =>
            Task.FromResult(new InputOperationDto(request.Port,
                request.Segments.Sum(segment => segment.Frames), _frame));

        public Task<OperationResultDto> ClearInputAsync(CancellationToken token)
        {
            ClearCalls++;
            _cleared = true;
            return Task.FromResult(new OperationResultDto(true, _frame, "cleared"));
        }

        public Task<ModTelemetryDto[]> ListModsAsync(CancellationToken token) => Task.FromResult(new[]
        {
            new ModTelemetryDto("coop", "Co-op", "0.4.0", "test", "test", [], true, true, 1, false, null),
        });

        public async Task<EntityListDto> ListEntitiesAsync(int maximum, CancellationToken token)
        {
            EvidenceTokens.Add(token);
            if (HangEntities) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            if (EntityFailure is not null) throw EntityFailure;
            EntityTelemetryDto[] entities = Enumerable.Range(0, 300).Select(index =>
                new EntityTelemetryDto(index, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16))
                .ToArray();
            return new EntityListDto(_frame, entities.Length, entities);
        }

        public Task<LogsResult> GetLogsAsync(int maximum, CancellationToken token)
        {
            EvidenceTokens.Add(token);
            return Task.FromResult(new LogsResult(1,
                Enumerable.Range(0, 300).Select(index => $"bridge-{index}-" + new string('x', 5000)).ToArray(),
                Enumerable.Range(0, 300).Select(index => $"process-{index}-" + new string('y', 5000)).ToArray()));
        }

        public Task<ScreenshotDto> CaptureScreenshotAsync(CancellationToken token)
        {
            EvidenceTokens.Add(token);
            byte[] bytes = InvalidScreenshot ? [1, 2, 3] : Png;
            return Task.FromResult(new ScreenshotDto(_frame, 2, 1, "image/png", "display",
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), Convert.ToBase64String(bytes)));
        }

        public Task<ScenarioBuildIdentity> GetBuildIdentityAsync(CancellationToken token)
        {
            BuildIdentityCalls++;
            return Task.FromResult(new ScenarioBuildIdentity("game.bin", new string('a', 64),
                "1.2.3+test", AutomationProtocol.Version, 321, "running"));
        }

        private static byte[] CreatePng()
        {
            byte[] png = new byte[24];
            byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
            signature.CopyTo(png, 0);
            "IHDR"u8.CopyTo(png.AsSpan(12));
            BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16, 4), 2);
            BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20, 4), 1);
            return png;
        }
    }

    private sealed class FailingWriter(string failedFile, string privateMessage) : IScenarioArtifactFileWriter
    {
        private readonly ScenarioArtifactFileWriter _inner = new();

        public Task WriteAsync(string directory, string fileName, ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken) => fileName == failedFile
            ? Task.FromException(new IOException(privateMessage))
            : _inner.WriteAsync(directory, fileName, bytes, cancellationToken);
    }

    private sealed class RecordingWriter : IScenarioArtifactFileWriter
    {
        private readonly ScenarioArtifactFileWriter _inner = new();
        public List<(string FileName, CancellationToken Token)> Writes { get; } = [];

        public Task WriteAsync(string directory, string fileName, ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken)
        {
            Writes.Add((fileName, cancellationToken));
            return _inner.WriteAsync(directory, fileName, bytes, cancellationToken);
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"sotn-scenario-{Guid.NewGuid():N}");

        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
