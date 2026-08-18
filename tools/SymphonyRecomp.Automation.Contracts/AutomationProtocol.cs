using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SymphonyRecomp.Automation.Contracts;

public static class AutomationProtocol
{
    public const string Version = "1.0";
    public const int MaxFrameBytes = 16 * 1024 * 1024;

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public static async ValueTask WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken = default)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (payload.Length > MaxFrameBytes)
            throw new InvalidDataException($"Automation frame exceeds {MaxFrameBytes} bytes.");

        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<T?> ReadAsync<T>(Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[4];
        int first = await stream.ReadAsync(header.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
        if (first == 0) return default;
        await ReadExactlyAsync(stream, header, first, cancellationToken).ConfigureAwait(false);

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxFrameBytes)
            throw new InvalidDataException($"Invalid automation frame length {length}.");

        byte[] payload = new byte[length];
        await ReadExactlyAsync(stream, payload, 0, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload, Json);
    }

    private static async ValueTask ReadExactlyAsync(Stream stream, byte[] buffer, int alreadyRead,
        CancellationToken cancellationToken)
    {
        int offset = alreadyRead;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Automation connection closed mid-frame.");
            offset += read;
        }
    }
}

public sealed record AutomationRequest(
    string Id,
    string Token,
    string Method,
    JsonElement? Parameters = null,
    int TimeoutMs = 5000);

public sealed record AutomationResponse(
    string Id,
    bool Success,
    JsonElement? Result = null,
    AutomationError? Error = null);

public sealed record AutomationError(string Code, string Message);

public sealed record BridgeStatusDto(
    string ProtocolVersion,
    bool Ready,
    long Frame,
    int ProcessId,
    string RuntimeState,
    string? GameState,
    string? Stage,
    int PendingCommands,
    bool InputActive);

public sealed record RuntimeTelemetryDto(
    long Frame,
    int ProcessId,
    long UptimeMilliseconds,
    int PendingCommands,
    string[] ActiveOverlays,
    int LogVersion,
    CpuTelemetryDto? Cpu,
    GpuTelemetryDto? Gpu,
    CdTelemetryDto? Cd);

public sealed record CpuTelemetryDto(
    uint[] Registers,
    uint Hi,
    uint Lo,
    uint Status,
    uint Cause,
    uint Epc,
    uint BadVAddr,
    uint ProcessorId);

public sealed record GpuTelemetryDto(
    bool DisplayEnabled,
    int DisplayX,
    int DisplayY,
    int DisplayWidth,
    int DisplayHeight,
    bool Display24Bit,
    bool Pal,
    bool HleActive,
    bool HleReady);

public sealed record CdTelemetryDto(bool Available, string? DiscPath);

public sealed record GameTelemetryDto(
    bool Available,
    string State,
    string EngineStep,
    uint GameStepRaw,
    uint EngineStepRaw,
    string? Character,
    string? Stage,
    int Area,
    int Room,
    int RoomX,
    int RoomY,
    bool Loading,
    bool MenuOpen,
    bool MapOpen,
    int CameraX,
    int CameraY,
    PlayerTelemetryDto? Player,
    InputTelemetryDto? Input,
    string? Seed,
    string? Preset);

public sealed record PlayerTelemetryDto(
    int X,
    int Y,
    int ScreenX,
    int ScreenY,
    int VelocityX,
    int VelocityY,
    bool FacingLeft,
    string Step,
    uint Status,
    bool HasControl,
    bool Invincible,
    int Hp,
    int HpMax,
    int Mp,
    int MpMax,
    int Hearts,
    int HeartsMax,
    int Level,
    int Experience,
    int Gold,
    int Kills);

public sealed record InputTelemetryDto(
    ushort Pressed,
    ushort Tapped,
    ushort Pressed2,
    ushort Tapped2,
    ushort AutomationMask1,
    ushort AutomationMask2,
    int AutomationFrames1,
    int AutomationFrames2);

public sealed record CombinedTelemetryDto(RuntimeTelemetryDto Runtime, GameTelemetryDto Game);

public sealed record ModTelemetryDto(
    string Id,
    string Name,
    string Version,
    string Author,
    string Description,
    string[] Dependencies,
    bool Enabled,
    bool Loaded,
    int HookCount,
    bool HasSettings,
    string? LoadError);

public sealed record EntityTelemetryDto(
    int Slot,
    ushort EntityId,
    ushort EnemyId,
    int X,
    int Y,
    int VelocityX,
    int VelocityY,
    ushort Step,
    ushort StepSub,
    ushort Parameters,
    uint Flags,
    ushort HitboxState,
    int HitPoints,
    int Attack,
    byte HitboxWidth,
    byte HitboxHeight,
    uint UpdateFunction);

public sealed record EntityListDto(long Frame, int TotalActive, EntityTelemetryDto[] Entities);

public sealed record LogSnapshotDto(int Version, string[] Lines);

public sealed record ScreenshotDto(
    long Frame,
    int Width,
    int Height,
    string MimeType,
    string Source,
    string Sha256,
    string Base64Data);

public sealed record MemoryReadRequest(uint Address, int Length);
public sealed record MemoryReadDto(uint Address, int Length, string Base64Data, string Hex);

public sealed record ModMutationRequest(string Id, bool Enabled, bool Confirm);
public sealed record ModReloadRequest(string Id, bool Confirm);
public sealed record ConfirmRequest(bool Confirm);

public sealed record InputTimelineRequest(int Port, InputSegmentDto[] Segments);
public sealed record InputSegmentDto(ushort Buttons, int Frames);
public sealed record InputOperationDto(int Port, int TotalFrames, long StartsAfterFrame);

public sealed record OperationResultDto(bool Applied, long Frame, string Message);
