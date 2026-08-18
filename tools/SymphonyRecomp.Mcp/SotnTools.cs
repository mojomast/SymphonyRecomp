using System.Buffers.Binary;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SymphonyRecomp.Automation.Contracts;

namespace SymphonyRecomp.Mcp;

[McpServerToolType]
public sealed partial class SotnTools(GameProcessManager process, GameAutomationClient client)
{
    private const int MaxScreenshotBytes = 8 * 1024 * 1024;
    private static readonly IReadOnlyDictionary<string, ushort> ButtonBits = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
    {
        ["L2"] = 0x0001, ["R2"] = 0x0002, ["L1"] = 0x0004, ["R1"] = 0x0008,
        ["Triangle"] = 0x0010, ["Circle"] = 0x0020, ["Cross"] = 0x0040, ["Square"] = 0x0080,
        ["Select"] = 0x0100, ["L3"] = 0x0200, ["R3"] = 0x0400, ["Start"] = 0x0800,
        ["Up"] = 0x1000, ["Right"] = 0x2000, ["Down"] = 0x4000, ["Left"] = 0x8000,
    };

    [McpServerTool(Name = "sotn_launch_game", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Launch the configured SymphonyRecomp executable with its configured disc and wait for the automation bridge to become ready. Paths are read only from server environment configuration.")]
    public Task<ProcessStatusResult> LaunchGame(CancellationToken cancellationToken) =>
        process.LaunchAsync(client, cancellationToken);

    [McpServerTool(Name = "sotn_stop_game", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Stop only the game process launched by this MCP server. Requires confirm=true because unsaved game progress can be lost.")]
    public Task<ProcessStatusResult> StopGame(
        [Description("Must be true to stop the managed game process.")] bool confirm,
        CancellationToken cancellationToken)
    {
        RequireConfirmation(confirm);
        return process.StopAsync(client, cancellationToken);
    }

    [McpServerTool(Name = "sotn_process_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Report managed-process, bridge-connection, and configured-file availability without exposing full paths or credentials.")]
    public ProcessStatusResult ProcessStatus() => process.GetStatus(client);

    [McpServerTool(Name = "sotn_get_state", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get current runtime, game, player, and input telemetry from the game bridge.")]
    public async Task<CombinedTelemetryDto> GetState(CancellationToken cancellationToken) =>
        SanitizeTelemetry(await client.GetTelemetryAsync(cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "sotn_wait_for_state", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Poll every 100 ms until all supplied game-state, stage, in-game, and raw engine-step predicates match, for at most 30 seconds.")]
    public async Task<CombinedTelemetryDto> WaitForState(
        [Description("Optional case-insensitive game state to match; maximum 64 characters.")] [MaxLength(64)] string? gameState = null,
        [Description("Optional case-insensitive stage identifier to match; maximum 64 characters.")] [MaxLength(64)] string? stage = null,
        [Description("Optional predicate requiring the Play game state when true or a non-Play state when false.")] bool? inGame = null,
        [Description("Optional full-width g_GameStep value to match.")] uint? gameStepRaw = null,
        [Description("Optional full-width g_GameEngineStep value to match, including file-select states above 0xFF.")] uint? engineStepRaw = null,
        [Description("Maximum wait in seconds, greater than zero and no more than 30.")] [Range(0.1, 30.0)] double timeoutSeconds = 10,
        CancellationToken cancellationToken = default)
    {
        gameState = ValidatePredicate(gameState, nameof(gameState));
        stage = ValidatePredicate(stage, nameof(stage));
        if (gameState == null && stage == null && inGame == null && gameStepRaw == null && engineStepRaw == null)
            throw new McpException("At least one state predicate is required.");
        if (!double.IsFinite(timeoutSeconds) || timeoutSeconds <= 0 || timeoutSeconds > 30)
            throw new McpException("timeoutSeconds must be greater than zero and no more than 30.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            while (true)
            {
                CombinedTelemetryDto telemetry = await client.GetTelemetryAsync(timeout.Token).ConfigureAwait(false);
                bool isInGame = string.Equals(telemetry.Game.State, "Play", StringComparison.OrdinalIgnoreCase);
                bool matches = (gameState == null || string.Equals(telemetry.Game.State, gameState, StringComparison.OrdinalIgnoreCase))
                    && (stage == null || string.Equals(telemetry.Game.Stage, stage, StringComparison.OrdinalIgnoreCase))
                    && (inGame == null || isInGame == inGame)
                    && (gameStepRaw == null || telemetry.Game.GameStepRaw == gameStepRaw)
                    && (engineStepRaw == null || telemetry.Game.EngineStepRaw == engineStepRaw);
                if (matches) return SanitizeTelemetry(telemetry);
                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpException("The requested game state was not reached before the wait timeout.");
        }
    }

    [McpServerTool(Name = "sotn_capture_screenshot", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ScreenshotMetadata))]
    [Description("Capture the current game display as a validated PNG image. Structured content contains metadata only; image bytes are returned as MCP image content.")]
    public async Task<CallToolResult> CaptureScreenshot(CancellationToken cancellationToken)
    {
        ScreenshotDto screenshot = await client.CaptureScreenshotAsync(cancellationToken).ConfigureAwait(false);
        byte[] png;
        try { png = Convert.FromBase64String(screenshot.Base64Data); }
        catch (FormatException) { throw new McpException("The game bridge returned invalid screenshot data."); }
        ValidatePng(screenshot, png);
        var metadata = new ScreenshotMetadata(screenshot.Frame, screenshot.Width, screenshot.Height,
            screenshot.MimeType, screenshot.Source, screenshot.Sha256, png.Length);
        return new CallToolResult
        {
            Content =
            [
                ImageContentBlock.FromBytes(png, "image/png"),
                new TextContentBlock { Text = JsonSerializer.Serialize(metadata, AutomationProtocol.Json) },
            ],
            StructuredContent = JsonSerializer.SerializeToElement(metadata, AutomationProtocol.Json),
        };
    }

    [McpServerTool(Name = "sotn_run_input", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Queue a bounded processed-pad input timeline. Button names are case-insensitive; an empty button array creates a neutral wait.")]
    public Task<InputOperationDto> RunInput(
        [Description("Controller port, either 0 or 1.")] [Range(0, 1)] int port,
        [Description("One to 120 input steps totaling no more than 1800 frames.")] [MinLength(1), MaxLength(120)] InputStep[] steps,
        CancellationToken cancellationToken)
    {
        if (port is < 0 or > 1) throw new McpException("port must be 0 or 1.");
        if (steps is not { Length: > 0 and <= 120 }) throw new McpException("steps must contain 1 to 120 entries.");
        int total = 0;
        var segments = new InputSegmentDto[steps.Length];
        for (int i = 0; i < steps.Length; i++)
        {
            InputStep step = steps[i] ?? throw new McpException($"steps[{i}] is required.");
            if (step.Frames is < 1 or > 1800) throw new McpException($"steps[{i}].frames must be between 1 and 1800.");
            total = checked(total + step.Frames);
            if (total > 1800) throw new McpException("The input timeline cannot exceed 1800 frames.");
            segments[i] = new InputSegmentDto(ParseButtons(step.Buttons, i), step.Frames);
        }
        return client.RunInputAsync(new InputTimelineRequest(port, segments), cancellationToken);
    }

    [McpServerTool(Name = "sotn_clear_input", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Immediately clear automation input on both controller ports.")]
    public Task<OperationResultDto> ClearInput(CancellationToken cancellationToken) => client.ClearInputAsync(cancellationToken);

    [McpServerTool(Name = "sotn_list_entities", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List active game entities, bounded to the requested maximum.")]
    public Task<EntityListDto> ListEntities(
        [Description("Maximum entity records to return, from 1 through 256.")] [Range(1, 256)] int maximum = 128,
        CancellationToken cancellationToken = default)
    {
        if (maximum is < 1 or > 256) throw new McpException("maximum must be between 1 and 256.");
        return client.ListEntitiesAsync(maximum, cancellationToken);
    }

    [McpServerTool(Name = "sotn_list_mods", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List installed mods and their current enabled and loaded state. This tool cannot install mods.")]
    public Task<ModTelemetryDto[]> ListMods(CancellationToken cancellationToken) => client.ListModsAsync(cancellationToken);

    [McpServerTool(Name = "sotn_set_mod_enabled", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Enable or disable an existing mod. Requires confirm=true and cannot install a mod.")]
    public Task<OperationResultDto> SetModEnabled(
        [Description("Existing mod identifier using letters, digits, period, underscore, or hyphen; maximum 128 characters.")] [MaxLength(128)] string id,
        [Description("True to enable the mod; false to disable it.")] bool enabled,
        [Description("Must be true to mutate mod state.")] bool confirm,
        CancellationToken cancellationToken)
    {
        RequireConfirmation(confirm);
        ValidateModId(id);
        return client.SetModEnabledAsync(new ModMutationRequest(id, enabled, true), cancellationToken);
    }

    [McpServerTool(Name = "sotn_reload_mod", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Reload an existing enabled mod. Requires confirm=true.")]
    public Task<OperationResultDto> ReloadMod(
        [Description("Existing mod identifier using letters, digits, period, underscore, or hyphen; maximum 128 characters.")] [MaxLength(128)] string id,
        [Description("Must be true to reload the mod.")] bool confirm,
        CancellationToken cancellationToken)
    {
        RequireConfirmation(confirm);
        ValidateModId(id);
        return client.ReloadModAsync(new ModReloadRequest(id, true), cancellationToken);
    }

    [McpServerTool(Name = "sotn_get_logs", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get bounded sanitized bridge logs and captured stdout/stderr from the managed process.")]
    public async Task<LogsResult> GetLogs(
        [Description("Maximum lines from each log source, from 1 through 1000.")] [Range(1, 1000)] int lines = 200,
        CancellationToken cancellationToken = default)
    {
        if (lines is < 1 or > 1000) throw new McpException("lines must be between 1 and 1000.");
        string[] processLines = process.GetProcessLogs(lines);
        try
        {
            LogSnapshotDto bridge = await client.GetLogsAsync(lines, cancellationToken).ConfigureAwait(false);
            return new LogsResult(bridge.Version, bridge.Lines.Select(process.SanitizeLogLine).ToArray(), processLines);
        }
        catch (McpException) when (processLines.Length > 0)
        {
            return new LogsResult(-1, [], processLines);
        }
    }

    [McpServerTool(Name = "sotn_read_memory", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read up to 4096 bytes from emulated PlayStation RAM. The address must use an explicit 0x hexadecimal prefix; memory writes are not supported.")]
    public async Task<MemoryResult> ReadMemory(
        [Description("RAM address with an explicit 0x prefix and one to eight hexadecimal digits, such as 0x80097C98.")] [MaxLength(10)] string address,
        [Description("Number of bytes to read, from 1 through 4096.")] [Range(1, 4096)] int length,
        CancellationToken cancellationToken)
    {
        if (address == null || !AddressPattern().IsMatch(address)
            || !uint.TryParse(address.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint parsed))
            throw new McpException("address must start with 0x and contain one to eight hexadecimal digits.");
        if (length is < 1 or > 4096) throw new McpException("length must be between 1 and 4096.");
        MemoryReadDto result = await client.ReadMemoryAsync(new MemoryReadRequest(parsed, length), cancellationToken).ConfigureAwait(false);
        return new MemoryResult($"0x{result.Address:X8}", result.Length, result.Hex);
    }

    [McpServerTool(Name = "sotn_hard_reset", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Hard-reset the emulated console and clear automation input. Requires confirm=true because unsaved progress can be lost.")]
    public Task<OperationResultDto> HardReset(
        [Description("Must be true to hard-reset the emulated console.")] bool confirm,
        CancellationToken cancellationToken)
    {
        RequireConfirmation(confirm);
        return client.HardResetAsync(cancellationToken);
    }

    private static ushort ParseButtons(string[]? buttons, int stepIndex)
    {
        if (buttons == null) throw new McpException($"steps[{stepIndex}].buttons is required; use an empty array for a neutral wait.");
        if (buttons.Length > ButtonBits.Count) throw new McpException($"steps[{stepIndex}].buttons contains too many entries.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ushort mask = 0;
        foreach (string? button in buttons)
        {
            if (string.IsNullOrWhiteSpace(button) || !ButtonBits.TryGetValue(button, out ushort bit))
                throw new McpException($"steps[{stepIndex}] contains an unknown button name.");
            if (!seen.Add(button)) throw new McpException($"steps[{stepIndex}] contains a duplicate button name.");
            mask |= bit;
        }
        if ((mask & 0xA000) == 0xA000) throw new McpException($"steps[{stepIndex}] cannot press Left and Right together.");
        if ((mask & 0x5000) == 0x5000) throw new McpException($"steps[{stepIndex}] cannot press Up and Down together.");
        return mask;
    }

    private static void ValidatePng(ScreenshotDto screenshot, byte[] png)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (png.Length is < 24 or > MaxScreenshotBytes || !png.AsSpan(0, 8).SequenceEqual(signature)
            || !png.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            throw new McpException("The game bridge returned an invalid or oversized PNG screenshot.");
        int width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        int height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        if (width is <= 0 or > 1024 || height is <= 0 or > 512 || width != screenshot.Width || height != screenshot.Height)
            throw new McpException("The game bridge returned invalid screenshot dimensions.");
        if (!string.Equals(screenshot.MimeType, "image/png", StringComparison.OrdinalIgnoreCase))
            throw new McpException("The game bridge returned an unsupported screenshot type.");
        string hash = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
        if (!string.Equals(hash, screenshot.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new McpException("The game bridge screenshot failed its integrity check.");
    }

    private static CombinedTelemetryDto SanitizeTelemetry(CombinedTelemetryDto value) =>
        value with { Runtime = value.Runtime with { Cd = value.Runtime.Cd is null ? null : value.Runtime.Cd with { DiscPath = SafeFileName(value.Runtime.Cd.DiscPath) } } };

    private static string? SafeFileName(string? value) => string.IsNullOrWhiteSpace(value) ? null : Path.GetFileName(value);
    private static string? ValidatePredicate(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.Length > 64 || value.Any(char.IsControl)) throw new McpException($"{name} must contain at most 64 printable characters.");
        return value;
    }

    private static void ValidateModId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || !ModIdPattern().IsMatch(id))
            throw new McpException("id is not a valid mod identifier.");
    }

    private static void RequireConfirmation(bool confirm)
    {
        if (!confirm) throw new McpException("confirm must be true for this operation.");
    }

    [GeneratedRegex("^0x[0-9A-Fa-f]{1,8}$", RegexOptions.CultureInvariant)]
    private static partial Regex AddressPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ModIdPattern();
}

/// <summary>One processed-pad state held for a positive number of frames.</summary>
public sealed record InputStep(
    [property: Description("Case-insensitive button names. Use an empty array for neutral input."), MaxLength(16)] string[] Buttons,
    [property: Description("Duration of this step in frames, from 1 through 1800."), Range(1, 1800)] int Frames);

/// <summary>Validated metadata for an MCP screenshot image.</summary>
public sealed record ScreenshotMetadata(long Frame, int Width, int Height, string MimeType, string Source, string Sha256, int ByteLength);

/// <summary>Sanitized bounded logs from the bridge and managed process.</summary>
public sealed record LogsResult(int BridgeVersion, string[] BridgeLines, string[] ProcessLines);

/// <summary>RAM bytes represented as uppercase hexadecimal without a second base64 copy.</summary>
public sealed record MemoryResult(string Address, int Length, string Hex);
