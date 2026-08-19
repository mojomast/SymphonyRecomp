using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RecompOne.Runtime.Diagnostics;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SymphonyRecomp.Automation.Contracts;
using RuntimeApi = RecompOne.Runtime.Runtime;

namespace SymphonyRecomp.Automation;

internal sealed class AutomationGameThread : IDisposable
{
    const int MaxScreenshotWidth = 1024;
    const int MaxScreenshotHeight = 512;
    const int MaxScreenshotPayload = 8 * 1024 * 1024;
    const int MaxModDiagnosticsPayload = 64 * 1024;

    readonly Func<int> _pendingCount;
    readonly Func<int> _disconnectGeneration;
    readonly Stopwatch _uptime = Stopwatch.StartNew();
    readonly InputState[] _input = [new(), new()];
    readonly ConcurrentDictionary<AutomationBridge.PendingCommand, byte> _deferred = new();
    int _seenDisconnectGeneration;
    IMemory? _seenMemory;
    long _frame;
    bool _disposed;

    public AutomationGameThread(Func<int> pendingCount, Func<int> disconnectGeneration)
    {
        _pendingCount = pendingCount;
        _disconnectGeneration = disconnectGeneration;
        _seenDisconnectGeneration = disconnectGeneration();
    }

    public void OnVSync(VSyncEvent e)
    {
        _frame = e.Frame;
        ClearForDisconnectOrReset();
        for (int i = 0; i < _input.Length; i++) _input[i].Advance();
    }

    public void OnPadRead(PadReadEvent e)
    {
        ClearForDisconnectOrReset();
        if (_disposed || e.Port is < 0 or > 1) return;
        // PadReadEvent carries byte-swapped, active-low PSX input. Contract masks use Sotn.Button values.
        e.Buttons = (ushort)(e.Buttons & ~_input[e.Port].Mask);
    }

    void ClearForDisconnectOrReset()
    {
        int generation = _disconnectGeneration();
        var memory = RuntimeApi.Mem;
        if (generation != _seenDisconnectGeneration || RuntimeApi.HardResetPending ||
            !ReferenceEquals(memory, _seenMemory))
        {
            _seenDisconnectGeneration = generation;
            _seenMemory = memory;
            ClearInput();
        }
    }

    public void Execute(AutomationBridge.PendingCommand pending)
    {
        if (pending.Command.Method is "mods.set_enabled" or "mods.reload" or
            "mods.diagnostics.capture" or "mods.diagnostics.reset")
        {
            if (_disposed || !_deferred.TryAdd(pending, 0))
            {
                CancelDeferred(pending);
                return;
            }
            if (!RuntimeApi.TryEnqueueMainThreadAction(() =>
                {
                    try
                    {
                        if (!_disposed && pending.TryBeginExecution()) ExecuteNow(pending);
                    }
                    finally { _deferred.TryRemove(pending, out _); }
                }))
            {
                _deferred.TryRemove(pending, out _);
                pending.Completion.TrySetResult(new AutomationResponse(
                    pending.Command.Id, false,
                    Error: new AutomationError("queue_full", "Runtime main-thread action queue is full.")));
            }
            return;
        }
        if (pending.TryBeginExecution()) ExecuteNow(pending);
    }

    void ExecuteNow(AutomationBridge.PendingCommand pending)
    {
        try
        {
            object result = pending.Command.Method switch
            {
                "bridge.status" => Status(),
                "telemetry.get" => Telemetry(),
                "entities.list" => EntitiesList((int)pending.Command.Argument!),
                "mods.list" => ModsList(),
                "mods.set_enabled" => SetMod((ModMutationRequest)pending.Command.Argument!),
                "mods.reload" => ReloadMod((ModReloadRequest)pending.Command.Argument!),
                "mods.diagnostics.capture" => CaptureModDiagnostics(
                    (ModDiagnosticsCaptureRequest)pending.Command.Argument!),
                "mods.diagnostics.reset" => ResetModDiagnostics(
                    (ModDiagnosticsResetRequest)pending.Command.Argument!),
                "logs.read" => Logs((int)pending.Command.Argument!),
                "memory.read" => ReadMemory((MemoryReadRequest)pending.Command.Argument!),
                "input.timeline" => SetTimeline((InputTimelineRequest)pending.Command.Argument!),
                "input.clear" => ClearInputResult(),
                "screenshot.capture" => CaptureScreenshot(),
                "runtime.hard_reset" => HardReset(),
                _ => throw new SafeCommandException("unknown_method", "Unknown automation method."),
            };

            if (result is RawScreenshot screenshot)
            {
                _ = CompleteScreenshotAsync(pending, screenshot);
                return;
            }
            Complete(pending, result);
        }
        catch (SafeCommandException ex)
        {
            pending.Completion.TrySetResult(new AutomationResponse(
                pending.Command.Id, false, Error: new AutomationError(ex.Code, ex.Message)));
        }
        catch
        {
            pending.Completion.TrySetResult(new AutomationResponse(
                pending.Command.Id, false, Error: new AutomationError("request_failed", "Automation request failed.")));
        }
    }

    static void Complete(AutomationBridge.PendingCommand pending, object result)
    {
        JsonElement json = JsonSerializer.SerializeToElement(result, AutomationProtocol.Json);
        pending.Completion.TrySetResult(new AutomationResponse(pending.Command.Id, true, json));
    }

    async Task CompleteScreenshotAsync(AutomationBridge.PendingCommand pending, RawScreenshot screenshot)
    {
        try
        {
            ScreenshotDto dto = await Task.Run(() => EncodeScreenshot(screenshot)).ConfigureAwait(false);
            Complete(pending, dto);
        }
        catch
        {
            pending.Completion.TrySetResult(new AutomationResponse(
                pending.Command.Id, false, Error: new AutomationError("capture_failed", "Screenshot capture failed.")));
        }
    }

    BridgeStatusDto Status()
    {
        bool ready = RuntimeApi.Cpu != null && RuntimeApi.Mem is PSMemory;
        string? gameState = null;
        string? stage = null;
        if (Sotn.Game.Available)
        {
            gameState = Sotn.Game.State.ToString();
            if (Sotn.Game.InGame) stage = Sotn.Game.StageId.ToString();
        }
        return new BridgeStatusDto(
            AutomationProtocol.Version, ready, _frame, Environment.ProcessId,
            ready ? "running" : "initializing", gameState, stage, _pendingCount(), InputActive);
    }

    CombinedTelemetryDto Telemetry() => new(RuntimeTelemetry(), GameTelemetry());

    RuntimeTelemetryDto RuntimeTelemetry()
    {
        var cpu = RuntimeApi.Cpu;
        CpuTelemetryDto? cpuDto = null;
        if (cpu != null)
        {
            var snapshot = cpu.Snapshot();
            cpuDto = new CpuTelemetryDto(snapshot.gpr, snapshot.hi, snapshot.lo, cpu.SR, cpu.Cause,
                cpu.EPC, cpu.BadVAddr, cpu.PRId);
        }

        var gpu = RuntimeApi.Gpu;
        GpuTelemetryDto? gpuDto = gpu == null ? null : new GpuTelemetryDto(
            gpu.DisplayEnabled, gpu.DisplayX, gpu.DisplayY, gpu.DisplayWidth, gpu.DisplayHeight,
            gpu.Display24Bit, gpu.Pal, GpuHle.Active, GpuHle.Backend?.Ready == true);
        var cd = RuntimeApi.Cd;
        var cdDto = new CdTelemetryDto(cd != null, cd != null ? RuntimeApi.ActiveCdPath : null);
        return new RuntimeTelemetryDto(
            _frame, Environment.ProcessId, _uptime.ElapsedMilliseconds, _pendingCount(),
            Dispatcher.ActiveNames, ConsoleMirror.Version, cpuDto, gpuDto, cdDto);
    }

    GameTelemetryDto GameTelemetry()
    {
        if (!Sotn.Game.Available)
            return EmptyGame(false, "unavailable");

        var state = Sotn.Game.State;
        uint gameStepRaw = RuntimeApi.Mem!.ReadU32(0x80073060);
        uint engineStepRaw = RuntimeApi.Mem.ReadU32(Sotn.Game.EngineStepAddr);
        if (!Sotn.Game.InGame)
            return EmptyGame(true, state.ToString(), Sotn.Game.EngineStep.ToString(), gameStepRaw, engineStepRaw);

        PlayerTelemetryDto? player = null;
        if (Sotn.Entities.Player.IsAlive)
        {
            player = new PlayerTelemetryDto(
                Sotn.Player.PosX, Sotn.Player.PosY, Sotn.Player.ScreenX, Sotn.Player.ScreenY,
                Sotn.Player.VelocityX, Sotn.Player.VelocityY, Sotn.Player.FacingLeft,
                Sotn.Player.Step.ToString(), (uint)Sotn.Player.Status, Sotn.Player.HasControl,
                Sotn.Player.IsInvincible, Sotn.Player.Hp, Sotn.Player.HpMax, Sotn.Player.Mp,
                Sotn.Player.MpMax, Sotn.Player.Hearts, Sotn.Player.HeartsMax, Sotn.Player.Level,
                Sotn.Player.Exp, Sotn.Player.Gold, Sotn.Player.KillCount);
        }

        return new GameTelemetryDto(
            true, state.ToString(), Sotn.Game.EngineStep.ToString(), gameStepRaw, engineStepRaw,
            Sotn.Game.Character.ToString(),
            Sotn.Game.StageId.ToString(), Sotn.Game.Area, Sotn.Game.Room, Sotn.Game.RoomX,
            Sotn.Game.RoomY, Sotn.Game.IsLoading, Sotn.Game.MenuOpen, Sotn.Game.MapOpen,
            Sotn.Game.CameraX, Sotn.Game.CameraY, player,
            new InputTelemetryDto(
                Sotn.Game.Pressed, Sotn.Game.Tapped, Sotn.Game.Pressed2, Sotn.Game.Tapped2,
                _input[0].Mask, _input[1].Mask, _input[0].RemainingFrames, _input[1].RemainingFrames),
            SafeGameString(() => Sotn.Game.SeedName), SafeGameString(() => Sotn.Game.PresetName));
    }

    static GameTelemetryDto EmptyGame(bool available, string state, string engineStep = "unavailable",
        uint gameStepRaw = 0, uint engineStepRaw = 0) =>
        new(available, state, engineStep, gameStepRaw, engineStepRaw, null, null, 0, 0, 0, 0, false, false, false, 0, 0,
            null, null, null, null);

    static string? SafeGameString(Func<string> read)
    {
        try
        {
            string value = read();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch { return null; }
    }

    EntityListDto EntitiesList(int maximum)
    {
        if (!Sotn.Game.Available || !Sotn.Game.InGame)
            return new EntityListDto(_frame, 0, []);
        var entities = new List<EntityTelemetryDto>(Math.Min(maximum, Sotn.Entities.Count));
        int active = 0;
        for (int slot = 0; slot < Sotn.Entities.Count; slot++)
        {
            var entity = Sotn.Entities.At(slot);
            if (!entity.IsAlive) continue;
            active++;
            if (entities.Count >= maximum) continue;
            entities.Add(new EntityTelemetryDto(
                slot, entity.EntityId, entity.EnemyId, entity.PosX, entity.PosY,
                entity.VelocityX, entity.VelocityY, entity.Step, entity.StepSub, entity.Params,
                (uint)entity.Flags, entity.HitboxState, entity.HitPoints, entity.Attack,
                entity.HitboxWidth, entity.HitboxHeight, entity.Update));
        }
        return new EntityListDto(_frame, active, entities.ToArray());
    }

    static ModTelemetryDto[] ModsList() => ModLoader.Mods.Select(ToModDto).ToArray();

    static ModTelemetryDto ToModDto(ModEntry mod) => new(
        mod.Info.Id, mod.Info.Name, mod.Info.Version, mod.Info.Author, mod.Info.Description,
        mod.Info.Dependencies.ToArray(), mod.Enabled, mod.Loaded, mod.HookCount, mod.HasSettings,
        string.IsNullOrWhiteSpace(mod.LoadError) ? null : "Mod failed to load.");

    OperationResultDto SetMod(ModMutationRequest request)
    {
        var mod = FindMod(request.Id);
        ModLoader.SetEnabled(mod.Info.Id, request.Enabled);
        mod = FindMod(request.Id);
        if (mod.Enabled != request.Enabled || request.Enabled != mod.Loaded)
            throw new SafeCommandException("mod_failed", request.Enabled ? "Mod did not load." : "Mod did not unload.");
        return new OperationResultDto(true, _frame, request.Enabled ? "Mod enabled." : "Mod disabled.");
    }

    OperationResultDto ReloadMod(ModReloadRequest request)
    {
        var mod = FindMod(request.Id);
        if (!mod.Enabled) throw new SafeCommandException("invalid_state", "Mod must be enabled before reload.");
        ModLoader.Reload(mod.Info.Id);
        mod = FindMod(request.Id);
        if (!mod.Loaded || mod.LoadError != null)
            throw new SafeCommandException("mod_failed", "Mod reload failed; inspect logs.");
        return new OperationResultDto(true, _frame, "Mod reloaded.");
    }

    ModDiagnosticsDto CaptureModDiagnostics(ModDiagnosticsCaptureRequest request)
    {
        ModEntry mod = FindMod(request.Id);
        if (!mod.Loaded) throw new SafeCommandException("invalid_state", "Mod is not loaded.");
        string payload;
        try
        {
            if (!ModLoader.TryCaptureAutomationDiagnostics(mod.Info.Id, _frame, out payload))
                throw new SafeCommandException("unsupported", "Mod does not expose structured diagnostics.");
        }
        catch (SafeCommandException) { throw; }
        catch { throw new SafeCommandException("provider_failed", "Mod diagnostics provider failed."); }
        return ParseModDiagnostics(mod.Info.Id, _frame, payload);
    }

    ModDiagnosticsResetDto ResetModDiagnostics(ModDiagnosticsResetRequest request)
    {
        ModEntry mod = FindMod(request.Id);
        if (!mod.Loaded) throw new SafeCommandException("invalid_state", "Mod is not loaded.");
        try
        {
            if (!ModLoader.TryResetAutomationDiagnostics(mod.Info.Id, request.SessionId,
                    request.ExpectedGeneration, out bool reset))
                throw new SafeCommandException("unsupported", "Mod does not expose structured diagnostics.");
            if (!reset)
                throw new SafeCommandException("stale_diagnostic_identity",
                    "Diagnostic session or generation no longer matches.");
        }
        catch (SafeCommandException) { throw; }
        catch { throw new SafeCommandException("provider_failed", "Mod diagnostics provider failed."); }
        return new ModDiagnosticsResetDto(mod.Info.Id, _frame, true);
    }

    internal static ModDiagnosticsDto ParseModDiagnostics(string id, long frame, string payload)
    {
        if (payload == null || Encoding.UTF8.GetByteCount(payload) > MaxModDiagnosticsPayload)
            throw new SafeCommandException("invalid_diagnostics", "Mod diagnostics exceeded the response limit.");
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new SafeCommandException("invalid_diagnostics", "Mod diagnostics must be a JSON object.");
            if (!document.RootElement.TryGetProperty("sessionId", out JsonElement sessionElement) ||
                sessionElement.ValueKind != JsonValueKind.String || sessionElement.GetString() is not { } sessionId ||
                sessionId.Length != 32 || !sessionId.All(Uri.IsHexDigit))
                throw new SafeCommandException("invalid_diagnostics", "Mod diagnostics had an invalid session identity.");
            if (!document.RootElement.TryGetProperty("generation", out JsonElement generationElement) ||
                !generationElement.TryGetInt32(out int generation) || generation < 0)
                throw new SafeCommandException("invalid_diagnostics", "Mod diagnostics had an invalid generation.");
            var result = new ModDiagnosticsDto(id, frame, sessionId, generation, document.RootElement.Clone());
            if (JsonSerializer.SerializeToUtf8Bytes(result, AutomationProtocol.Json).Length > MaxModDiagnosticsPayload)
                throw new SafeCommandException("invalid_diagnostics", "Mod diagnostics exceeded the response limit.");
            return result;
        }
        catch (SafeCommandException) { throw; }
        catch (JsonException)
        {
            throw new SafeCommandException("invalid_diagnostics", "Mod diagnostics were not valid JSON.");
        }
    }

    static ModEntry FindMod(string id) => ModLoader.Mods.FirstOrDefault(
        mod => string.Equals(mod.Info.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new SafeCommandException("not_found", "Mod was not found.");

    static LogSnapshotDto Logs(int maximum)
    {
        var lines = new List<string>();
        int version = ConsoleMirror.SnapshotInto(lines);
        if (lines.Count > maximum) lines.RemoveRange(0, lines.Count - maximum);
        return new LogSnapshotDto(version, lines.ToArray());
    }

    static MemoryReadDto ReadMemory(MemoryReadRequest request)
    {
        if (RuntimeApi.Mem is not PSMemory memory)
            throw new SafeCommandException("unavailable", "PSMemory RAM is unavailable.");
        uint physical = MemoryMap.ToPhysical(request.Address);
        uint length = (uint)request.Length;
        uint end;
        try { end = checked(physical + length); }
        catch (OverflowException) { throw new SafeCommandException("out_of_range", "Memory range is outside RAM."); }
        if (physical >= memory.Ram.Length || end > memory.Ram.Length)
            throw new SafeCommandException("out_of_range", "Memory range is outside RAM.");
        byte[] data = memory.Ram.Slice((int)physical, request.Length).ToArray();
        return new MemoryReadDto(request.Address, data.Length, Convert.ToBase64String(data), Convert.ToHexString(data));
    }

    InputOperationDto SetTimeline(InputTimelineRequest request)
    {
        int total = request.Segments.Sum(segment => segment.Frames);
        _input[request.Port].Replace(request.Segments);
        return new InputOperationDto(request.Port, total, _frame);
    }

    OperationResultDto ClearInputResult()
    {
        ClearInput();
        return new OperationResultDto(true, _frame, "Automation input cleared.");
    }

    OperationResultDto HardReset()
    {
        ClearInput();
        RuntimeApi.HardReset();
        return new OperationResultDto(true, _frame, "Hard reset requested.");
    }

    RawScreenshot CaptureScreenshot()
    {
        var gpu = RuntimeApi.Gpu ?? throw new SafeCommandException("unavailable", "GPU display is unavailable.");
        int width = gpu.DisplayWidth;
        int height = gpu.DisplayHeight;
        if (!gpu.DisplayEnabled || width <= 0 || height <= 0)
            throw new SafeCommandException("unavailable", "GPU display is unavailable.");
        if (width > MaxScreenshotWidth || height > MaxScreenshotHeight ||
            (long)width * height * 3 > MaxScreenshotPayload)
            throw new SafeCommandException("capture_too_large", "GPU display exceeds screenshot limits.");

        ushort[] vram;
        string source;
        if (GpuHle.Active && GpuHle.Backend is { Ready: true } backend)
        {
            vram = new ushort[RecompOne.Runtime.Gpu.VramWidth * RecompOne.Runtime.Gpu.VramHeight];
            backend.ReadVram(0, 0, RecompOne.Runtime.Gpu.VramWidth, RecompOne.Runtime.Gpu.VramHeight, vram);
            source = "hle-vram";
        }
        else
        {
            vram = gpu.Vram;
            source = "software-vram";
        }

        byte[] rgb = ConvertDisplay(vram, gpu.DisplayX, gpu.DisplayY, width, height, gpu.Display24Bit);
        return new RawScreenshot(_frame, width, height, source, rgb);
    }

    static byte[] ConvertDisplay(ushort[] vram, int displayX, int displayY, int width, int height, bool display24Bit)
    {
        int vramWidth = RecompOne.Runtime.Gpu.VramWidth;
        int vramHeight = RecompOne.Runtime.Gpu.VramHeight;
        byte[] rgb = new byte[width * height * 3];
        int output = 0;
        if (display24Bit)
        {
            for (int y = 0; y < height; y++)
            {
                int lineByte = (((displayY + y) & (vramHeight - 1)) * vramWidth + displayX) * 2;
                for (int x = 0; x < width; x++)
                {
                    int offset = lineByte + x * 3;
                    rgb[output++] = VramByte(vram, offset);
                    rgb[output++] = VramByte(vram, offset + 1);
                    rgb[output++] = VramByte(vram, offset + 2);
                }
            }
        }
        else
        {
            for (int y = 0; y < height; y++)
            {
                int line = ((displayY + y) & (vramHeight - 1)) * vramWidth;
                for (int x = 0; x < width; x++)
                {
                    ushort pixel = vram[line + ((displayX + x) & (vramWidth - 1))];
                    rgb[output++] = (byte)((pixel & 0x1f) << 3);
                    rgb[output++] = (byte)(((pixel >> 5) & 0x1f) << 3);
                    rgb[output++] = (byte)(((pixel >> 10) & 0x1f) << 3);
                }
            }
        }
        return rgb;
    }

    static byte VramByte(ushort[] vram, int byteOffset)
    {
        int index = (byteOffset >> 1) & (vram.Length - 1);
        ushort value = vram[index];
        return (byte)((byteOffset & 1) == 0 ? value : value >> 8);
    }

    static ScreenshotDto EncodeScreenshot(RawScreenshot screenshot)
    {
        using var image = Image.LoadPixelData<Rgb24>(screenshot.Rgb, screenshot.Width, screenshot.Height);
        using var output = new MemoryStream();
        image.SaveAsPng(output);
        if (output.Length > MaxScreenshotPayload)
            throw new InvalidDataException("Screenshot payload exceeds limit.");
        byte[] png = output.ToArray();
        return new ScreenshotDto(
            screenshot.Frame, screenshot.Width, screenshot.Height, "image/png", screenshot.Source,
            Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant(), Convert.ToBase64String(png));
    }

    bool InputActive => _input[0].RemainingFrames > 0 || _input[1].RemainingFrames > 0;

    void ClearInput()
    {
        _input[0].Clear();
        _input[1].Clear();
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (AutomationBridge.PendingCommand pending in _deferred.Keys)
            CancelDeferred(pending);
        ClearInput();
    }

    internal static void CancelDeferred(AutomationBridge.PendingCommand pending)
    {
        if (pending.TryCancel())
            pending.Completion.TrySetResult(new AutomationResponse(
                pending.Command.Id, false, Error: new AutomationError("shutdown", "Automation bridge stopped.")));
    }

    sealed class InputState
    {
        InputSegmentDto[] _segments = [];
        int _index;
        int _segmentFrames;

        public ushort Mask => _index < _segments.Length ? _segments[_index].Buttons : (ushort)0;
        public int RemainingFrames { get; private set; }

        public void Replace(InputSegmentDto[] segments)
        {
            _segments = segments.ToArray();
            _index = 0;
            _segmentFrames = _segments[0].Frames;
            RemainingFrames = _segments.Sum(segment => segment.Frames);
        }

        public void Advance()
        {
            if (RemainingFrames <= 0) return;
            RemainingFrames--;
            _segmentFrames--;
            if (_segmentFrames > 0) return;
            _index++;
            if (_index < _segments.Length) _segmentFrames = _segments[_index].Frames;
            else Clear();
        }

        public void Clear()
        {
            _segments = [];
            _index = 0;
            _segmentFrames = 0;
            RemainingFrames = 0;
        }
    }

    sealed record RawScreenshot(long Frame, int Width, int Height, string Source, byte[] Rgb);

    sealed class SafeCommandException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
