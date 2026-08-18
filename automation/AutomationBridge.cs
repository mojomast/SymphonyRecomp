using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RecompOne.Runtime.Events;
using SymphonyRecomp.Automation.Contracts;

namespace SymphonyRecomp.Automation;

public sealed class AutomationBridge : IDisposable
{
    const int QueueCapacity = 32;
    const int CommandsPerFrame = 8;
    const int MinTimeoutMs = 100;
    const int MaxTimeoutMs = 15_000;

    static readonly HashSet<string> NoArgumentMethods = new(StringComparer.Ordinal)
    {
        "bridge.status", "telemetry.get", "mods.list", "input.clear",
        "screenshot.capture",
    };

    readonly string _pipeName;
    readonly byte[] _tokenHash;
    readonly ConcurrentQueue<PendingCommand> _commands = new();
    readonly CancellationTokenSource _shutdown = new();
    readonly AutomationGameThread _gameThread;
    readonly Task _ioTask;
    NamedPipeServerStream? _activePipe;
    int _pendingCount;
    int _disconnectGeneration;
    bool _disposed;

    public AutomationBridge(string pipeName, string token)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
            throw new ArgumentException("Automation pipe name is required.", nameof(pipeName));
        if (token.Length != 64 || !token.All(Uri.IsHexDigit))
            throw new ArgumentException("Automation token must be a 64-character hexadecimal value.", nameof(token));

        _pipeName = pipeName;
        byte[] tokenBytes = Encoding.UTF8.GetBytes(token);
        _tokenHash = SHA256.HashData(tokenBytes);
        CryptographicOperations.ZeroMemory(tokenBytes);
        _gameThread = new AutomationGameThread(
            () => Volatile.Read(ref _pendingCount),
            () => Volatile.Read(ref _disconnectGeneration));
        Event.AddListener<VSyncEvent>(OnVSync);
        Event.AddListener<PadReadEvent>(_gameThread.OnPadRead);
        _ioTask = Task.Run(RunServerAsync);
    }

    void OnVSync(VSyncEvent e)
    {
        _gameThread.OnVSync(e);
        for (int i = 0; i < CommandsPerFrame && _commands.TryDequeue(out var pending); i++)
        {
            ReleaseQueueSlot(pending);
            if (!pending.IsCanceled)
                _gameThread.Execute(pending);
        }
    }

    async Task RunServerAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                _activePipe = pipe;
                await pipe.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                await ServeClientAsync(pipe, _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (IOException) when (!_shutdown.IsCancellationRequested) { }
            catch (Exception) when (!_shutdown.IsCancellationRequested)
            {
                Console.Error.WriteLine("[Automation] pipe connection failed.");
            }
            finally
            {
                _activePipe = null;
                Interlocked.Increment(ref _disconnectGeneration);
            }
        }
    }

    async Task ServeClientAsync(Stream pipe, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            AutomationRequest? request;
            try
            {
                request = await AutomationProtocol.ReadAsync<AutomationRequest>(pipe, cancellationToken).ConfigureAwait(false);
                if (request == null) return;
            }
            catch (EndOfStreamException) { return; }
            catch (IOException) { return; }
            catch (JsonException)
            {
                await WriteErrorAsync(pipe, "", "invalid_request", "Invalid request frame.", cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (InvalidDataException)
            {
                await WriteErrorAsync(pipe, "", "invalid_request", "Invalid request frame.", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!TokenMatches(request.Token))
            {
                await WriteErrorAsync(pipe, SafeId(request.Id), "unauthorized", "Authentication failed.", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!TryValidate(request, out var command, out var validationError))
            {
                await WriteErrorAsync(pipe, SafeId(request.Id), "invalid_request", validationError, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var pending = new PendingCommand(command!);
            if (Interlocked.Increment(ref _pendingCount) > QueueCapacity)
            {
                Interlocked.Decrement(ref _pendingCount);
                await WriteErrorAsync(pipe, command!.Id, "queue_full", "Automation command queue is full.", cancellationToken).ConfigureAwait(false);
                continue;
            }
            _commands.Enqueue(pending);

            AutomationResponse response;
            using var disconnectMonitor = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task disconnected = WaitForDisconnectAsync(pipe, disconnectMonitor.Token);
            Task timedOut = Task.Delay(command!.TimeoutMs, cancellationToken);
            try
            {
                Task finished = await Task.WhenAny(pending.Completion.Task, disconnected, timedOut).ConfigureAwait(false);
                if (finished == disconnected)
                {
                    if (pending.TryCancel()) pending.Completion.TrySetCanceled();
                    Interlocked.Increment(ref _disconnectGeneration);
                    return;
                }
                if (finished == timedOut)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    disconnectMonitor.Cancel();
                    await IgnoreCancellationAsync(disconnected).ConfigureAwait(false);
                    if (pending.TryCancel())
                    {
                        pending.Completion.TrySetCanceled();
                        response = Error(command.Id, "timeout", "Automation request timed out before execution.");
                    }
                    else
                    {
                        response = Error(command.Id, "outcome_unknown",
                            "Automation request timed out after execution began; inspect current state before retrying.");
                    }
                }
                else
                {
                    disconnectMonitor.Cancel();
                    await IgnoreCancellationAsync(disconnected).ConfigureAwait(false);
                    response = await pending.Completion.Task.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                pending.Completion.TrySetCanceled();
                return;
            }

            try { await AutomationProtocol.WriteAsync(pipe, response, cancellationToken).ConfigureAwait(false); }
            catch (IOException) { return; }
        }
    }

    static async Task WaitForDisconnectAsync(Stream pipe, CancellationToken cancellationToken)
    {
        byte[] unexpected = new byte[1];
        try
        {
            int read = await pipe.ReadAsync(unexpected, cancellationToken).ConfigureAwait(false);
            if (read != 0) throw new InvalidDataException("Pipelined automation requests are not supported.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException) { }
    }

    static async Task IgnoreCancellationAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    void ReleaseQueueSlot(PendingCommand pending)
    {
        if (Interlocked.Exchange(ref pending.Counted, 0) == 1)
            Interlocked.Decrement(ref _pendingCount);
    }

    bool TokenMatches(string? supplied)
    {
        if (supplied == null) return false;
        byte[] candidate = Encoding.UTF8.GetBytes(supplied);
        byte[] candidateHash = SHA256.HashData(candidate);
        CryptographicOperations.ZeroMemory(candidate);
        bool matches = CryptographicOperations.FixedTimeEquals(candidateHash, _tokenHash);
        CryptographicOperations.ZeroMemory(candidateHash);
        return matches;
    }

    static bool TryValidate(AutomationRequest request, out AutomationCommand? command, out string error)
    {
        command = null;
        error = "Invalid request.";
        string id = SafeId(request.Id);
        if (id.Length == 0 || id.Length > 128 || id.Any(char.IsControl))
        {
            error = "Request id must contain 1 to 128 characters.";
            return false;
        }
        if (request.TimeoutMs is < MinTimeoutMs or > MaxTimeoutMs)
        {
            error = $"timeoutMs must be between {MinTimeoutMs} and {MaxTimeoutMs}.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(request.Method) || request.Method.Length > 64)
        {
            error = "Method is required.";
            return false;
        }

        try
        {
            object? argument = ValidateParameters(request.Method, request.Parameters);
            command = new AutomationCommand(id, request.Method, argument, request.TimeoutMs);
            return true;
        }
        catch (AutomationValidationException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (JsonException)
        {
            error = "Invalid method parameters.";
            return false;
        }
    }

    static object? ValidateParameters(string method, JsonElement? parameters)
    {
        if (NoArgumentMethods.Contains(method))
        {
            RequireProperties(parameters, []);
            return null;
        }

        return method switch
        {
            "entities.list" => ReadBoundedCount(parameters, "max", 256, 256),
            "logs.read" => ReadBoundedCount(parameters, "lines", 200, 1000),
            "memory.read" => ReadMemoryRequest(parameters),
            "mods.set_enabled" => ReadModMutation(parameters),
            "mods.reload" => ReadModReload(parameters),
            "input.timeline" => ReadTimeline(parameters),
            "runtime.hard_reset" => ReadConfirmation(parameters),
            _ => throw new AutomationValidationException("Unknown automation method."),
        };
    }

    static int ReadBoundedCount(JsonElement? parameters, string name, int defaultValue, int maximum)
    {
        RequireProperties(parameters, [name]);
        if (parameters is not { ValueKind: JsonValueKind.Object } p || !TryProperty(p, name, out var value))
            return defaultValue;
        if (!value.TryGetInt32(out int count) || count is < 1 || count > maximum)
            throw new AutomationValidationException($"{name} must be between 1 and {maximum}.");
        return count;
    }

    static MemoryReadRequest ReadMemoryRequest(JsonElement? parameters)
    {
        RequireProperties(parameters, ["address", "length"], requireObject: true, requireAll: true);
        var value = parameters!.Value.Deserialize<MemoryReadRequest>(AutomationProtocol.Json)
            ?? throw new AutomationValidationException("Memory parameters are required.");
        if (value.Length is < 1 or > 4096)
            throw new AutomationValidationException("length must be between 1 and 4096.");
        return value;
    }

    static ModMutationRequest ReadModMutation(JsonElement? parameters)
    {
        RequireProperties(parameters, ["id", "enabled", "confirm"], requireObject: true, requireAll: true);
        var value = parameters!.Value.Deserialize<ModMutationRequest>(AutomationProtocol.Json)
            ?? throw new AutomationValidationException("Mod parameters are required.");
        ValidateModId(value.Id);
        if (!value.Confirm) throw new AutomationValidationException("confirm must be true.");
        return value;
    }

    static ModReloadRequest ReadModReload(JsonElement? parameters)
    {
        RequireProperties(parameters, ["id", "confirm"], requireObject: true, requireAll: true);
        var value = parameters!.Value.Deserialize<ModReloadRequest>(AutomationProtocol.Json)
            ?? throw new AutomationValidationException("Mod parameters are required.");
        ValidateModId(value.Id);
        if (!value.Confirm) throw new AutomationValidationException("confirm must be true.");
        return value;
    }

    static ConfirmRequest ReadConfirmation(JsonElement? parameters)
    {
        RequireProperties(parameters, ["confirm"], requireObject: true, requireAll: true);
        var value = parameters!.Value.Deserialize<ConfirmRequest>(AutomationProtocol.Json)
            ?? throw new AutomationValidationException("Confirmation is required.");
        if (!value.Confirm) throw new AutomationValidationException("confirm must be true.");
        return value;
    }

    static InputTimelineRequest ReadTimeline(JsonElement? parameters)
    {
        RequireProperties(parameters, ["port", "segments"], requireObject: true, requireAll: true);
        if (!TryProperty(parameters!.Value, "segments", out var segments) || segments.ValueKind != JsonValueKind.Array)
            throw new AutomationValidationException("segments must be an array.");
        foreach (var segment in segments.EnumerateArray())
            RequireProperties(segment, ["buttons", "frames"], requireObject: true, requireAll: true);
        var value = parameters!.Value.Deserialize<InputTimelineRequest>(AutomationProtocol.Json)
            ?? throw new AutomationValidationException("Input timeline parameters are required.");
        if (value.Port is < 0 or > 1) throw new AutomationValidationException("port must be 0 or 1.");
        if (value.Segments is not { Length: > 0 and <= 120 })
            throw new AutomationValidationException("segments must contain 1 to 120 entries.");
        int total = 0;
        foreach (var segment in value.Segments)
        {
            if (segment.Frames <= 0) throw new AutomationValidationException("Each segment duration must be positive.");
            try { total = checked(total + segment.Frames); }
            catch (OverflowException) { throw new AutomationValidationException("Timeline duration is too large."); }
            if (total > 1800) throw new AutomationValidationException("Timeline duration cannot exceed 1800 frames.");
        }
        return new InputTimelineRequest(value.Port, value.Segments.ToArray());
    }

    static void ValidateModId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 ||
            !Regex.IsMatch(id, "^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant))
            throw new AutomationValidationException("Invalid mod id.");
    }

    static void RequireProperties(JsonElement? parameters, string[] allowed, bool requireObject = false, bool requireAll = false)
    {
        if (parameters is null || parameters.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            if (requireObject) throw new AutomationValidationException("Method parameters are required.");
            return;
        }
        if (parameters.Value.ValueKind != JsonValueKind.Object)
            throw new AutomationValidationException("Method parameters must be an object.");
        var names = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in parameters.Value.EnumerateObject())
        {
            if (!names.Contains(property.Name))
                throw new AutomationValidationException($"Unknown parameter '{property.Name}'.");
            if (!seen.Add(property.Name))
                throw new AutomationValidationException($"Duplicate parameter '{property.Name}'.");
        }
        if (requireAll && seen.Count != names.Count)
            throw new AutomationValidationException("One or more required parameters are missing.");
    }

    static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    static string SafeId(string? id) => id?.Trim() ?? "";

    static AutomationResponse Error(string id, string code, string message) =>
        new(id, false, Error: new AutomationError(code, message));

    static ValueTask WriteErrorAsync(Stream pipe, string id, string code, string message, CancellationToken token) =>
        AutomationProtocol.WriteAsync(pipe, Error(id, code, message), token);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        Interlocked.Increment(ref _disconnectGeneration);
        Event.RemoveListener<VSyncEvent>(OnVSync);
        Event.RemoveListener<PadReadEvent>(_gameThread.OnPadRead);
        _gameThread.Dispose();
        try { _activePipe?.Dispose(); } catch { }
        try { _ioTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _shutdown.Dispose();
        CryptographicOperations.ZeroMemory(_tokenHash);
        while (_commands.TryDequeue(out var pending))
        {
            ReleaseQueueSlot(pending);
            pending.Completion.TrySetResult(Error(pending.Command.Id, "shutdown", "Automation bridge stopped."));
        }
        Volatile.Write(ref _pendingCount, 0);
    }

    internal sealed record AutomationCommand(string Id, string Method, object? Argument, int TimeoutMs);

    internal sealed class PendingCommand
    {
        public PendingCommand(AutomationCommand command) => Command = command;
        public AutomationCommand Command { get; }
        public int Counted = 1;
        int _state;
        public bool IsCanceled => Volatile.Read(ref _state) == 2;
        public bool TryBeginExecution() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;
        public bool TryCancel() => Interlocked.CompareExchange(ref _state, 2, 0) == 0;
        public TaskCompletionSource<AutomationResponse> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    sealed class AutomationValidationException(string message) : Exception(message);
}
