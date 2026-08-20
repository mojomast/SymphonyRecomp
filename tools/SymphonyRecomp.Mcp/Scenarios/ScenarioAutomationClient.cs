using SymphonyRecomp.Automation.Contracts;

namespace SymphonyRecomp.Mcp.Scenarios;

public sealed class ScenarioAutomationClient(GameAutomationClient client, GameProcessManager process)
    : IScenarioAutomationClient
{
    public Task<BridgeStatusDto> GetBridgeStatusAsync(CancellationToken token) =>
        client.GetBridgeStatusAsync(token);

    public Task<CombinedTelemetryDto> GetTelemetryAsync(CancellationToken token) =>
        client.GetTelemetryAsync(token);

    public Task<ModDiagnosticsDto> CaptureModDiagnosticsAsync(ModDiagnosticsCaptureRequest request,
        CancellationToken token) => client.CaptureModDiagnosticsAsync(request, token);

    public Task<ModDiagnosticsResetDto> ResetModDiagnosticsAsync(ModDiagnosticsResetRequest request,
        CancellationToken token) => client.ResetModDiagnosticsAsync(request, token);

    public Task<InputOperationDto> RunInputAsync(InputTimelineRequest request, CancellationToken token) =>
        client.RunInputAsync(request, token);

    public Task<InputBatchOperationDto> RunInputBatchAsync(InputBatchRequest request, CancellationToken token) =>
        client.RunInputBatchAsync(request, token);

    public Task<OperationResultDto> ClearInputAsync(CancellationToken token) => client.ClearInputAsync(token);
    public Task<ModTelemetryDto[]> ListModsAsync(CancellationToken token) => client.ListModsAsync(token);
    public Task<EntityListDto> ListEntitiesAsync(int maximum, CancellationToken token) =>
        client.ListEntitiesAsync(Math.Clamp(maximum, 1, 256), token);

    public async Task<LogsResult> GetLogsAsync(int maximum, CancellationToken token)
    {
        maximum = Math.Clamp(maximum, 1, 200);
        string[] processLines = process.GetProcessLogs(maximum);
        try
        {
            LogSnapshotDto bridge = await client.GetLogsAsync(maximum, token).ConfigureAwait(false);
            return new LogsResult(bridge.Version,
                bridge.Lines.Take(maximum).Select(process.SanitizeLogLine).ToArray(), processLines);
        }
        catch (ModelContextProtocol.McpException) when (processLines.Length > 0)
        {
            return new LogsResult(-1, [], processLines);
        }
    }

    public Task<ScreenshotDto> CaptureScreenshotAsync(CancellationToken token) =>
        client.CaptureScreenshotAsync(token);

    public Task<ScenarioBuildIdentity> GetBuildIdentityAsync(CancellationToken token) =>
        process.GetBuildIdentityAsync(client, token);
}
