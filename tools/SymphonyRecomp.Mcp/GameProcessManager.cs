using System.Diagnostics;
using System.Security.Cryptography;

namespace SymphonyRecomp.Mcp;

public sealed class GameProcessManager : IAsyncDisposable
{
    private const int ReadinessTimeoutSeconds = 30;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly BoundedLogRing _logs = new(2000);
    private readonly string? _executable = ReadPath("SYMPHONYRECOMP_EXECUTABLE");
    private readonly string? _disc = ReadPath("SYMPHONYRECOMP_DISC");
    private readonly string? _workdir = ReadPath("SYMPHONYRECOMP_WORKDIR");
    private Process? _process;
    private Task? _stdoutDrain;
    private Task? _stderrDrain;
    private string? _token;

    public bool IsRunning => TryGetRunning(_process, out _);

    public async Task<ProcessStatusResult> LaunchAsync(GameAutomationClient client, CancellationToken cancellationToken)
    {
        if (!await _lifecycle.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new ModelContextProtocol.McpException("A process lifecycle operation is already running.");
        try
        {
            if (IsRunning) throw new ModelContextProtocol.McpException("The managed game process is already running.");
            DisposeExitedProcess();
            ValidateConfiguration();

            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            string pipeName = $"sotn-{Environment.ProcessId}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}";
            var startInfo = new ProcessStartInfo
            {
                FileName = _executable!,
                WorkingDirectory = _workdir ?? Path.GetDirectoryName(_executable!)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            ConfigureChildEnvironment(startInfo);
            startInfo.ArgumentList.Add("--automation");
            startInfo.ArgumentList.Add("--disc");
            startInfo.ArgumentList.Add(_disc!);
            startInfo.Environment["SYMPHONYRECOMP_AUTOMATION_PIPE"] = pipeName;
            startInfo.Environment["SYMPHONYRECOMP_AUTOMATION_TOKEN"] = token;

            var process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start()) throw new InvalidOperationException("The game process did not start.");
                _process = process;
                _token = token;
                _stdoutDrain = DrainAsync(process.StandardOutput, "stdout");
                _stderrDrain = DrainAsync(process.StandardError, "stderr");
                client.Configure(pipeName, token, () => IsRunning);
                using var readyCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readyCts.CancelAfter(TimeSpan.FromSeconds(ReadinessTimeoutSeconds));
                await client.WaitForBridgeReadyAsync(readyCts.Token).ConfigureAwait(false);
                return GetStatus(client);
            }
            catch
            {
                client.Clear();
                await KillOwnedProcessAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch (ModelContextProtocol.McpException) { throw; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelContextProtocol.McpException("The game did not become automation-ready within 30 seconds.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new ModelContextProtocol.McpException("The configured game process could not be launched.");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task<ProcessStatusResult> StopAsync(GameAutomationClient client, CancellationToken cancellationToken)
    {
        if (!await _lifecycle.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new ModelContextProtocol.McpException("A process lifecycle operation is already running.");
        try
        {
            client.Clear();
            await KillOwnedProcessAsync().ConfigureAwait(false);
            return GetStatus(client);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public ProcessStatusResult GetStatus(GameAutomationClient client)
    {
        Process? process = _process;
        bool running = TryGetRunning(process, out int processId);
        return new ProcessStatusResult(
            process != null,
            running,
            client.IsConfigured,
            client.IsConnected,
            running ? processId : null,
            !string.IsNullOrWhiteSpace(_executable) && File.Exists(_executable),
            !string.IsNullOrWhiteSpace(_disc) && File.Exists(_disc),
            string.IsNullOrWhiteSpace(_workdir) || Directory.Exists(_workdir),
            SafeFileName(_executable),
            SafeFileName(_disc),
            string.IsNullOrWhiteSpace(_workdir) ? null : SafeFileName(_workdir));
    }

    public string[] GetProcessLogs(int maximum) => _logs.Snapshot(maximum, Sanitize);

    public string SanitizeLogLine(string value)
    {
        string sanitized = Sanitize(value);
        return sanitized.Length > 4096 ? sanitized[..4096] : sanitized;
    }

    private void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_executable) || !File.Exists(_executable))
            throw new ModelContextProtocol.McpException("SYMPHONYRECOMP_EXECUTABLE must name an existing executable file.");
        if (string.IsNullOrWhiteSpace(_disc) || !File.Exists(_disc))
            throw new ModelContextProtocol.McpException("SYMPHONYRECOMP_DISC must name an existing disc file.");
        if (!string.IsNullOrWhiteSpace(_workdir) && !Directory.Exists(_workdir))
            throw new ModelContextProtocol.McpException("SYMPHONYRECOMP_WORKDIR must name an existing directory when set.");
    }

    private async Task DrainAsync(StreamReader reader, string source)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                _logs.Add(source, Sanitize(line));
        }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }

    internal static void ConfigureChildEnvironment(ProcessStartInfo startInfo)
    {
        string[] allowed =
        [
            "PATH", "DOTNET_ROOT", "DOTNET_BUNDLE_EXTRACT_BASE_DIR",
            "SystemRoot", "WINDIR", "COMSPEC", "PATHEXT",
            "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "PROGRAMDATA",
            "TEMP", "TMP", "TMPDIR",
            "LANG", "LC_ALL", "LC_CTYPE",
            "DISPLAY", "WAYLAND_DISPLAY", "XAUTHORITY", "XDG_RUNTIME_DIR",
            "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME", "DBUS_SESSION_BUS_ADDRESS",
            "PULSE_SERVER", "LD_LIBRARY_PATH", "DYLD_LIBRARY_PATH",
            "LIBGL_DRIVERS_PATH", "MESA_LOADER_DRIVER_OVERRIDE", "__GLX_VENDOR_LIBRARY_NAME",
            "VK_ICD_FILENAMES", "SDL_AUDIODRIVER", "SDL_VIDEODRIVER",
        ];
        var inherited = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in allowed)
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
                inherited[name] = value;

        startInfo.Environment.Clear();
        foreach (var pair in inherited) startInfo.Environment[pair.Key] = pair.Value;
    }

    private async Task KillOwnedProcessAsync()
    {
        Process? process = _process;
        if (process == null) return;
        try
        {
            if (TryGetRunning(process, out _))
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException) { }
        catch (TimeoutException)
        {
            throw new ModelContextProtocol.McpException("The managed game process did not stop within five seconds.");
        }
        finally
        {
            if (!TryGetRunning(process, out _))
            {
                if (_stdoutDrain != null) await IgnoreFailureAsync(_stdoutDrain).ConfigureAwait(false);
                if (_stderrDrain != null) await IgnoreFailureAsync(_stderrDrain).ConfigureAwait(false);
                _stdoutDrain = null;
                _stderrDrain = null;
                if (ReferenceEquals(_process, process)) _process = null;
                process.Dispose();
                _token = null;
            }
        }
    }

    private void DisposeExitedProcess()
    {
        if (_process is not { HasExited: true } process) return;
        process.Dispose();
        _process = null;
        _token = null;
    }

    private string Sanitize(string value)
    {
        foreach (string? secret in new[] { _token, _executable, _disc, _workdir })
            if (!string.IsNullOrEmpty(secret)) value = value.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return value;
    }

    private static string? ReadPath(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value);
    }

    private static string? SafeFileName(string? path) => string.IsNullOrWhiteSpace(path)
        ? null
        : Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    private static bool TryGetRunning(Process? process, out int processId)
    {
        processId = 0;
        if (process == null) return false;
        try
        {
            if (process.HasExited) return false;
            processId = process.Id;
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }

    private static async Task IgnoreFailureAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await KillOwnedProcessAsync().ConfigureAwait(false); }
        finally
        {
            _lifecycle.Release();
            _lifecycle.Dispose();
        }
    }
}

/// <summary>Sanitized status of the game process owned by this MCP server.</summary>
public sealed record ProcessStatusResult(
    bool Launched,
    bool Running,
    bool BridgeConfigured,
    bool Connected,
    int? ProcessId,
    bool ExecutableAvailable,
    bool DiscAvailable,
    bool WorkDirectoryAvailable,
    string? ExecutableFileName,
    string? DiscFileName,
    string? WorkDirectoryName);
