using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SymphonyRecomp.Automation.Contracts;

namespace SymphonyRecomp.Mcp.Scenarios;

public interface IScenarioExecutionService
{
    Task<ScenarioExecutionResult> RunAsync(string source, CancellationToken cancellationToken = default);
}

public sealed class ScenarioExecutionService : IScenarioExecutionService
{
    public const string ManifestSchema = "sotn-scenario-artifacts/1";
    public const string RunnerVersion = "1";
    private const int MaximumJsonArtifactBytes = 1024 * 1024;
    private const int MaximumDiagnosticsBytes = 64 * 1024;
    private const int MaximumLogsBytes = 2 * 1024 * 1024;
    private const int MaximumLogLineCharacters = 4096;
    private static readonly TimeSpan PostRunEvidenceTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MandatoryWriteTimeout = TimeSpan.FromSeconds(2);

    private readonly IScenarioAutomationClient _client;
    private readonly IScenarioClock _clock;
    private readonly string _artifactRoot;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<ScenarioDefinition, DateTimeOffset, string> _runIdFactory;
    private readonly IScenarioArtifactFileWriter _writer;
    private readonly TimeSpan _postRunEvidenceTimeout;
    private readonly TimeSpan _mandatoryWriteTimeout;

    public ScenarioExecutionService(IScenarioAutomationClient client, IScenarioClock clock)
        : this(client, clock, ResolveArtifactRoot(), static () => DateTimeOffset.UtcNow,
            DefaultRunId, new ScenarioArtifactFileWriter()) { }

    internal ScenarioExecutionService(IScenarioAutomationClient client, IScenarioClock clock,
        string artifactRoot, Func<DateTimeOffset> utcNow,
        Func<ScenarioDefinition, DateTimeOffset, string> runIdFactory,
        IScenarioArtifactFileWriter? writer = null,
        TimeSpan? postRunEvidenceTimeout = null,
        TimeSpan? mandatoryWriteTimeout = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _artifactRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            artifactRoot ?? throw new ArgumentNullException(nameof(artifactRoot))));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _runIdFactory = runIdFactory ?? throw new ArgumentNullException(nameof(runIdFactory));
        _writer = writer ?? new ScenarioArtifactFileWriter();
        _postRunEvidenceTimeout = postRunEvidenceTimeout ?? PostRunEvidenceTimeout;
        _mandatoryWriteTimeout = mandatoryWriteTimeout ?? MandatoryWriteTimeout;
    }

    public async Task<ScenarioExecutionResult> RunAsync(string source,
        CancellationToken cancellationToken = default)
    {
        ScenarioDefinition scenario = ScenarioParser.Parse(source);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] sourceBytes = Encoding.UTF8.GetBytes(source);
        string sourceHash = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();
        DateTimeOffset started = _utcNow().ToUniversalTime();
        string runId = _runIdFactory(scenario, started);
        ValidateRunId(runId);

        Directory.CreateDirectory(_artifactRoot);
        RejectReparsePoint(_artifactRoot);
        string finalDirectory = DirectChild(_artifactRoot, runId);
        if (Directory.Exists(finalDirectory) || File.Exists(finalDirectory))
            throw new InvalidOperationException("A scenario artifact run with this identifier already exists.");
        string temporaryDirectory = CreateTemporaryDirectory(runId);

        try
        {
            await WriteMandatoryAsync(temporaryDirectory, "scenario.json", sourceBytes)
                .ConfigureAwait(false);

            var errors = new List<ScenarioArtifactError>(8);
            ScenarioBuildIdentity build = await CaptureBuildIdentityAsync(errors, cancellationToken)
                .ConfigureAwait(false);
            ScenarioModIdentity? mod = await CaptureModIdentityAsync(scenario.ModId, errors,
                cancellationToken).ConfigureAwait(false);
            ScenarioRunResult run = await new ScenarioRunner(_client, _clock)
                .RunAsync(scenario, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<ScenarioArtifact> policy = run.Outcome == ScenarioRunOutcome.Passed
                ? scenario.Artifacts.OnSuccess
                : scenario.Artifacts.OnFailure;
            string[] requested = policy.Select(ArtifactName).ToArray();
            var captured = new List<string>(requested.Length);
            ModDiagnosticsDto? capturedDiagnostics = null;
            using var evidenceBudget = new CancellationTokenSource(_postRunEvidenceTimeout);
            foreach (ScenarioArtifact artifact in policy)
            {
                try
                {
                    (string fileName, byte[] bytes, ModDiagnosticsDto? diagnostics) =
                        await CaptureArtifactAsync(artifact, scenario.ModId, evidenceBudget.Token)
                            .ConfigureAwait(false);
                    try
                    {
                        await _writer.WriteAsync(temporaryDirectory, fileName, bytes,
                            evidenceBudget.Token).ConfigureAwait(false);
                        captured.Add(ArtifactName(artifact));
                        capturedDiagnostics ??= diagnostics;
                    }
                    catch
                    {
                        AddError(errors, artifact, "write", "The artifact could not be written.");
                    }
                }
                catch
                {
                    AddError(errors, artifact, "capture", "The artifact could not be captured or validated.");
                }
            }

            ScenarioDiagnosticIdentity? diagnostic = run.FinalDiagnostics;
            if (capturedDiagnostics is not null)
            {
                string? schema = TryGetDiagnosticSchema(capturedDiagnostics.Payload);
                diagnostic = new ScenarioDiagnosticIdentity(capturedDiagnostics.SessionId,
                    capturedDiagnostics.Generation, capturedDiagnostics.Frame, schema);
            }

            DateTimeOffset ended = _utcNow().ToUniversalTime();
            var manifest = new ScenarioArtifactManifest(
                ManifestSchema,
                runId,
                new ScenarioSourceIdentity(scenario.Schema, scenario.Id, scenario.Version, sourceHash),
                new ScenarioRuntimeIdentity(RunnerVersion, build.McpInformationalVersion,
                    build.ProtocolVersion, build.ExecutableFileName, build.ExecutableSha256,
                    build.ProcessId, build.ProcessStatus),
                mod,
                diagnostic is null ? null : new ScenarioDiagnosticManifestIdentity(
                    diagnostic.Schema, diagnostic.SessionId, diagnostic.Generation, diagnostic.Frame),
                started,
                ended,
                run.Outcome.ToString(),
                run.FailedCheckpoint,
                run.PrimaryError,
                run.InitialFrame,
                run.FinalFrame,
                new ScenarioCleanupManifest(run.CleanupAttempted, run.CleanupSucceeded, run.CleanupVerified),
                requested,
                captured.ToArray(),
                errors.Take(16).ToArray());

            byte[] manifestBytes = SerializeBounded(manifest, MaximumJsonArtifactBytes);
            await WriteMandatoryAsync(temporaryDirectory, "manifest.json", manifestBytes)
                .ConfigureAwait(false);
            Directory.Move(temporaryDirectory, finalDirectory);
            return new ScenarioExecutionResult(runId, runId, run, manifest);
        }
        catch
        {
            TryDeleteTemporaryDirectory(temporaryDirectory);
            throw;
        }
    }

    private async Task<ScenarioBuildIdentity> CaptureBuildIdentityAsync(List<ScenarioArtifactError> errors,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            return await _client.GetBuildIdentityAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            errors.Add(new ScenarioArtifactError("identity", "capture",
                "The sanitized build identity was unavailable."));
            return new ScenarioBuildIdentity(null, null, "unknown", AutomationProtocol.Version,
                null, "unknown");
        }
    }

    private async Task<ScenarioModIdentity?> CaptureModIdentityAsync(string modId,
        List<ScenarioArtifactError> errors, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            ModTelemetryDto? mod = (await _client.ListModsAsync(timeout.Token).ConfigureAwait(false))
                .FirstOrDefault(value => value.Loaded && value.Id == modId);
            return mod is null ? null : new ScenarioModIdentity(mod.Id, Bound(mod.Version, 128));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            errors.Add(new ScenarioArtifactError("identity", "capture",
                "The loaded target mod identity was unavailable."));
            return null;
        }
    }

    private async Task<(string FileName, byte[] Bytes, ModDiagnosticsDto? Diagnostics)> CaptureArtifactAsync(
        ScenarioArtifact artifact, string modId, CancellationToken cancellationToken)
    {
        return artifact switch
        {
            ScenarioArtifact.State => ("state.json", SerializeBounded(SanitizeTelemetry(
                await _client.GetTelemetryAsync(cancellationToken).ConfigureAwait(false)),
                MaximumJsonArtifactBytes), null),
            ScenarioArtifact.Diagnostics => Diagnostics(await _client.CaptureModDiagnosticsAsync(
                new ModDiagnosticsCaptureRequest(modId), cancellationToken).ConfigureAwait(false)),
            ScenarioArtifact.Entities => ("entities.json", SerializeBounded(BoundEntities(
                await _client.ListEntitiesAsync(256, cancellationToken).ConfigureAwait(false)),
                MaximumJsonArtifactBytes), null),
            ScenarioArtifact.Logs => ("logs.json", SerializeBounded(BoundLogs(
                await _client.GetLogsAsync(200, cancellationToken).ConfigureAwait(false)),
                MaximumLogsBytes), null),
            ScenarioArtifact.Screenshot => ("screenshot.png", PngScreenshotValidator.DecodeAndValidate(
                await _client.CaptureScreenshotAsync(cancellationToken).ConfigureAwait(false)), null),
            _ => throw new InvalidDataException("Unsupported artifact policy."),
        };

        static (string, byte[], ModDiagnosticsDto?) Diagnostics(ModDiagnosticsDto value) =>
            ("diagnostics.json", SerializeBounded(value, MaximumDiagnosticsBytes), value);
    }

    private async Task WriteMandatoryAsync(string directory, string fileName, ReadOnlyMemory<byte> bytes)
    {
        using var timeout = new CancellationTokenSource(_mandatoryWriteTimeout);
        await _writer.WriteAsync(directory, fileName, bytes, timeout.Token).ConfigureAwait(false);
    }

    private string CreateTemporaryDirectory(string runId)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            string suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            string path = DirectChild(_artifactRoot, $".tmp-{runId}-{suffix}");
            if (Directory.Exists(path) || File.Exists(path)) continue;
            try
            {
                Directory.CreateDirectory(path);
                RejectReparsePoint(path);
                return path;
            }
            catch (IOException) when (attempt < 7) { }
        }
        throw new IOException("A temporary scenario artifact directory could not be created.");
    }

    private static byte[] SerializeBounded<T>(T value, int maximumBytes)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, AutomationProtocol.Json);
        if (bytes.Length > maximumBytes) throw new InvalidDataException("Artifact exceeds its size bound.");
        return bytes;
    }

    private static CombinedTelemetryDto SanitizeTelemetry(CombinedTelemetryDto value) =>
        value with { Runtime = value.Runtime with { Cd = value.Runtime.Cd is null ? null :
            value.Runtime.Cd with { DiscPath = SafeFileName(value.Runtime.Cd.DiscPath) } } };

    private static EntityListDto BoundEntities(EntityListDto value) =>
        value with { Entities = value.Entities.Take(256).ToArray() };

    private static LogsResult BoundLogs(LogsResult value) => new(value.BridgeVersion,
        value.BridgeLines.Take(200).Select(line => Bound(line, MaximumLogLineCharacters)).ToArray(),
        value.ProcessLines.Take(200).Select(line => Bound(line, MaximumLogLineCharacters)).ToArray());

    private static void AddError(List<ScenarioArtifactError> errors, ScenarioArtifact artifact,
        string stage, string message)
    {
        if (errors.Count < 16) errors.Add(new ScenarioArtifactError(ArtifactName(artifact), stage, message));
    }

    private static string ArtifactName(ScenarioArtifact artifact) => artifact.ToString().ToLowerInvariant();
    private static string? TryGetDiagnosticSchema(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("schema", out JsonElement schema) &&
        schema.ValueKind == JsonValueKind.String ? Bound(schema.GetString(), 128) : null;
    private static string Bound(string? value, int maximum) =>
        (value ?? string.Empty)[..Math.Min(value?.Length ?? 0, maximum)];
    private static string? SafeFileName(string? value) => string.IsNullOrWhiteSpace(value) ? null : Path.GetFileName(value);

    private static string ResolveArtifactRoot()
    {
        string? configured = Environment.GetEnvironmentVariable("SYMPHONYRECOMP_SCENARIO_ARTIFACTS");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.CurrentDirectory, "artifacts", "scenarios")
            : configured;
    }

    private static string DefaultRunId(ScenarioDefinition scenario, DateTimeOffset started) =>
        $"{scenario.Id}-{started:yyyyMMddTHHmmssfffZ}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}";

    private static void ValidateRunId(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || !IsAlphaNumeric(value[0]) ||
            value.Any(character => !IsAlphaNumeric(character) && character is not ('.' or '_' or '-')))
            throw new InvalidOperationException("The generated scenario run identifier is invalid.");
    }

    private static bool IsAlphaNumeric(char value) =>
        value is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static string DirectChild(string root, string name)
    {
        string path = Path.GetFullPath(Path.Combine(root, name));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(path), root, comparison))
            throw new InvalidOperationException("The scenario artifact directory is invalid.");
        return path;
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Scenario artifact directories cannot be symbolic links.");
    }

    private static void TryDeleteTemporaryDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }
}

internal interface IScenarioArtifactFileWriter
{
    Task WriteAsync(string directory, string fileName, ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken);
}

internal sealed class ScenarioArtifactFileWriter : IScenarioArtifactFileWriter
{
    public async Task WriteAsync(string directory, string fileName, ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(directory, fileName);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed record ScenarioExecutionResult(
    string RunId,
    string ArtifactId,
    ScenarioRunResult Run,
    ScenarioArtifactManifest Manifest);

public sealed record ScenarioArtifactManifest(
    string Schema,
    string RunId,
    ScenarioSourceIdentity Scenario,
    ScenarioRuntimeIdentity Runtime,
    ScenarioModIdentity? Mod,
    ScenarioDiagnosticManifestIdentity? Diagnostics,
    DateTimeOffset StartedUtc,
    DateTimeOffset EndedUtc,
    string Outcome,
    string? FirstFailedCheckpoint,
    string? PrimaryError,
    long? InitialFrame,
    long? FinalFrame,
    ScenarioCleanupManifest Cleanup,
    string[] RequestedArtifacts,
    string[] CapturedArtifacts,
    ScenarioArtifactError[] ArtifactErrors);

public sealed record ScenarioSourceIdentity(string Schema, string Id, string Version, string SourceSha256);
public sealed record ScenarioRuntimeIdentity(
    string RunnerVersion,
    string McpInformationalVersion,
    string ProtocolVersion,
    string? ExecutableFileName,
    string? ExecutableSha256,
    int? ProcessId,
    string ProcessStatus);
public sealed record ScenarioModIdentity(string Id, string Version);
public sealed record ScenarioDiagnosticManifestIdentity(string? Schema, string SessionId, int Generation, long Frame);
public sealed record ScenarioCleanupManifest(bool Attempted, bool Succeeded, bool Verified);
public sealed record ScenarioArtifactError(string Artifact, string Stage, string Message);
