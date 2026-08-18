using System.IO.Pipes;
using System.Text.Json;
using ModelContextProtocol;
using SymphonyRecomp.Automation.Contracts;

namespace SymphonyRecomp.Mcp;

public sealed class GameAutomationClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly SemaphoreSlim _admission = new(8, 8);
    private readonly object _configurationGate = new();
    private NamedPipeClientStream? _pipe;
    private string? _pipeName;
    private string? _token;
    private Func<bool>? _isProcessRunning;
    private long _pipeGeneration;
    private long _negotiatedGeneration;

    public GameAutomationClient()
    {
        string? pipeName = Environment.GetEnvironmentVariable("SYMPHONYRECOMP_AUTOMATION_PIPE");
        string? token = Environment.GetEnvironmentVariable("SYMPHONYRECOMP_AUTOMATION_TOKEN");
        if (!string.IsNullOrWhiteSpace(pipeName) && !string.IsNullOrEmpty(token))
            Configure(pipeName, token, static () => true);
    }

    public bool IsConfigured
    {
        get { lock (_configurationGate) return _pipeName != null && _token != null; }
    }

    public bool IsConnected
    {
        get { lock (_configurationGate) return _pipe?.IsConnected == true; }
    }

    public void Configure(string pipeName, string token, Func<bool> isProcessRunning)
    {
        if (string.IsNullOrWhiteSpace(pipeName) || pipeName.Length > 128 || pipeName.Any(char.IsControl))
            throw new ArgumentException("Invalid automation pipe name.", nameof(pipeName));
        if (token.Length != 64 || !token.All(Uri.IsHexDigit))
            throw new ArgumentException("Automation token must be a 64-character hexadecimal value.", nameof(token));
        lock (_configurationGate)
        {
            ClearPipe();
            _pipeName = pipeName;
            _token = token;
            _isProcessRunning = isProcessRunning;
            _negotiatedGeneration = 0;
        }
    }

    public void Clear()
    {
        lock (_configurationGate)
        {
            ClearPipe();
            _pipeName = null;
            _token = null;
            _isProcessRunning = null;
            _negotiatedGeneration = 0;
        }
    }

    public async Task WaitForBridgeReadyAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                BridgeStatusDto status = await ProbeReadyAsync(cancellationToken).ConfigureAwait(false);
                if (status.Ready && status.ProtocolVersion == AutomationProtocol.Version)
                    return;
            }
            catch (McpException) when (!IsConnected)
            {
                // The game may still be creating its pipe during startup.
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<BridgeStatusDto> GetBridgeStatusAsync(CancellationToken token) =>
        CallAsync<BridgeStatusDto>("bridge.status", null, 2000, token);
    public Task<CombinedTelemetryDto> GetTelemetryAsync(CancellationToken token) =>
        CallReadyAsync<CombinedTelemetryDto>("telemetry.get", null, 5000, token);
    public Task<EntityListDto> ListEntitiesAsync(int maximum, CancellationToken token) =>
        CallReadyAsync<EntityListDto>("entities.list", new { max = maximum }, 5000, token);
    public Task<ModTelemetryDto[]> ListModsAsync(CancellationToken token) =>
        CallReadyAsync<ModTelemetryDto[]>("mods.list", null, 5000, token);
    public Task<OperationResultDto> SetModEnabledAsync(ModMutationRequest request, CancellationToken token) =>
        CallReadyAsync<OperationResultDto>("mods.set_enabled", request, 5000, token);
    public Task<OperationResultDto> ReloadModAsync(ModReloadRequest request, CancellationToken token) =>
        CallReadyAsync<OperationResultDto>("mods.reload", request, 5000, token);
    public Task<LogSnapshotDto> GetLogsAsync(int lines, CancellationToken token) =>
        CallReadyAsync<LogSnapshotDto>("logs.read", new { lines }, 5000, token);
    public Task<MemoryReadDto> ReadMemoryAsync(MemoryReadRequest request, CancellationToken token) =>
        CallReadyAsync<MemoryReadDto>("memory.read", request, 5000, token);
    public Task<InputOperationDto> RunInputAsync(InputTimelineRequest request, CancellationToken token) =>
        CallReadyAsync<InputOperationDto>("input.timeline", request, 5000, token);
    public Task<OperationResultDto> ClearInputAsync(CancellationToken token) =>
        CallReadyAsync<OperationResultDto>("input.clear", null, 5000, token);
    public Task<ScreenshotDto> CaptureScreenshotAsync(CancellationToken token) =>
        CallReadyAsync<ScreenshotDto>("screenshot.capture", null, 15000, token);
    public Task<OperationResultDto> HardResetAsync(CancellationToken token) =>
        CallReadyAsync<OperationResultDto>("runtime.hard_reset", new ConfirmRequest(true), 5000, token);

    private async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (IsCurrentPipeNegotiated()) return;
        BridgeStatusDto status = await ProbeReadyAsync(cancellationToken).ConfigureAwait(false);
        if (!status.Ready) throw new McpException("The game bridge is connected but not ready.");
    }

    private async Task<BridgeStatusDto> ProbeReadyAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await CallAsync<BridgeStatusDto>("bridge.status", null, 2000, cancellationToken,
                    (status, connection) =>
                    {
                        if (status.ProtocolVersion != AutomationProtocol.Version)
                            throw new McpException("The game bridge uses an unsupported automation protocol version.");
                        if (status.Ready) CommitNegotiation(connection);
                    }).ConfigureAwait(false);
            }
            catch (BridgeReconnectException) when (attempt == 0) { }
        }
    }

    private async Task<T> CallReadyAsync<T>(string method, object? parameters, int timeoutMs,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await CallAsync<T>(method, parameters, timeoutMs, cancellationToken).ConfigureAwait(false);
            }
            catch (BridgeReconnectException) when (attempt == 0)
            {
                // A replacement pipe must negotiate before carrying an operational request.
            }
        }
    }

    private async Task<T> CallAsync<T>(string method, object? parameters, int timeoutMs,
        CancellationToken cancellationToken, Action<T, PipeConnection>? onSuccess = null)
    {
        if (!await _admission.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new McpException("Too many concurrent game automation requests.");
        try
        {
            await _requests.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs + 1000));
            PipeConnection connection = await EnsureConnectedAsync(timeout.Token).ConfigureAwait(false);
            NamedPipeClientStream pipe = connection.Pipe;
            if (method != "bridge.status" && !IsNegotiated(connection))
                throw new BridgeReconnectException();
            string id = Guid.NewGuid().ToString("N");
            string token;
            lock (_configurationGate) token = _token ?? throw Unavailable();
            JsonElement? json = parameters == null
                ? null
                : JsonSerializer.SerializeToElement(parameters, AutomationProtocol.Json);
            try
            {
                await AutomationProtocol.WriteAsync(pipe,
                    new AutomationRequest(id, token, method, json, timeoutMs), timeout.Token).ConfigureAwait(false);
                AutomationResponse? response = await AutomationProtocol.ReadAsync<AutomationResponse>(pipe, timeout.Token)
                    .ConfigureAwait(false);
                if (response == null) throw new EndOfStreamException();
                if (!string.Equals(response.Id, id, StringComparison.Ordinal))
                    throw new InvalidDataException("Mismatched automation response identifier.");
                if (!response.Success) throw BridgeError(response.Error);
                if (response.Result is not { } result)
                    throw new InvalidDataException("Automation response did not contain a result.");
                T value = result.Deserialize<T>(AutomationProtocol.Json)
                    ?? throw new InvalidDataException("Automation response had an invalid result.");
                onSuccess?.Invoke(value, connection);
                return value;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Disconnect();
                throw new McpException("The game bridge request timed out.");
            }
            catch (OperationCanceledException)
            {
                Disconnect();
                throw;
            }
            catch (McpException) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
            {
                Disconnect();
                throw new McpException("The game bridge connection was interrupted; the request outcome may be unknown.");
            }
            }
            finally
            {
                _requests.Release();
            }
        }
        finally
        {
            _admission.Release();
        }
    }

    private async Task<PipeConnection> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        lock (_configurationGate)
        {
            if (_pipe?.IsConnected == true) return new PipeConnection(_pipe, _pipeGeneration);
            ClearPipe();
            if (_pipeName == null || _token == null || _isProcessRunning?.Invoke() != true) throw Unavailable();
            _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            _pipeGeneration++;
            if (_pipeGeneration == 0) _pipeGeneration++;
        }

        PipeConnection candidate;
        lock (_configurationGate) candidate = new PipeConnection(_pipe!, _pipeGeneration);
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(TimeSpan.FromMilliseconds(500));
            await candidate.Pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
            return candidate;
        }
        catch
        {
            Disconnect();
            throw new McpException("The game automation bridge is not connected.");
        }
    }

    private void Disconnect()
    {
        lock (_configurationGate)
        {
            ClearPipe();
        }
    }

    private void ClearPipe()
    {
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
        _negotiatedGeneration = 0;
    }

    private bool IsCurrentPipeNegotiated()
    {
        lock (_configurationGate)
            return _pipe?.IsConnected == true && _pipeGeneration != 0 &&
                _negotiatedGeneration == _pipeGeneration;
    }

    private bool IsNegotiated(PipeConnection connection)
    {
        lock (_configurationGate)
            return ReferenceEquals(_pipe, connection.Pipe) && _pipeGeneration == connection.Generation &&
                _negotiatedGeneration == connection.Generation;
    }

    private void CommitNegotiation(PipeConnection connection)
    {
        lock (_configurationGate)
        {
            if (!ReferenceEquals(_pipe, connection.Pipe) || _pipeGeneration != connection.Generation)
                throw new BridgeReconnectException();
            _negotiatedGeneration = connection.Generation;
        }
    }

    private static McpException BridgeError(AutomationError? error)
    {
        string code = Sanitize(error?.Code, "request_failed", 48, code: true);
        string message = Sanitize(error?.Message, "The game bridge rejected the request.", 256, code: false);
        return new McpException($"Game bridge error ({code}): {message}");
    }

    private static string Sanitize(string? value, string fallback, int maximum, bool code)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        string safe = new(value.Where(ch => code
            ? char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-'
            : char.IsAsciiLetterOrDigit(ch) || char.IsWhiteSpace(ch) || ch is '.' or ',' or ':' or ';' or '(' or ')' or '_' or '-' or '\'').ToArray());
        if (safe.Length > maximum) safe = safe[..maximum];
        return safe.Length == 0 ? fallback : safe;
    }

    private static McpException Unavailable() => new("No managed game automation bridge is available. Launch the game first.");

    private sealed class BridgeReconnectException : Exception;
    private readonly record struct PipeConnection(NamedPipeClientStream Pipe, long Generation);

    public ValueTask DisposeAsync()
    {
        Clear();
        _admission.Dispose();
        _requests.Dispose();
        return ValueTask.CompletedTask;
    }
}
