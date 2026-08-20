using SymphonyRecomp.Automation.Contracts;

namespace SymphonyRecomp.Automation;

internal readonly record struct PlayerTelemetrySafety(
    bool MemoryAvailable,
    bool IsPlay,
    bool IsAlucard,
    bool IsLoading,
    bool MenuOpen,
    bool MapOpen,
    bool IsDefaultPlayStep,
    bool IsNormalEngineStep,
    bool CutsceneControlClear,
    bool SpecialTransitionClear,
    bool HasControl,
    int Hp,
    uint Status,
    ushort Step);

internal static class PlayerTelemetryPolicy
{
    internal const uint TransformStatusMask = 0x7;
    internal const uint DeadStatusMask = 0x40000;
    internal const ushort DeathStep = 0x10;

    internal static PlayerTelemetryDto? Capture(
        PlayerTelemetrySafety safety, Func<PlayerTelemetryDto> capture)
    {
        if (!IsSafeLiveAlucard(safety)) return null;

        try { return capture(); }
        catch { return null; }
    }

    internal static bool IsSafeLiveAlucard(PlayerTelemetrySafety safety) =>
        safety.MemoryAvailable && safety.IsPlay && safety.IsAlucard && !safety.IsLoading &&
        !safety.MenuOpen && !safety.MapOpen && safety.IsDefaultPlayStep &&
        safety.IsNormalEngineStep && safety.CutsceneControlClear &&
        safety.SpecialTransitionClear && safety.HasControl && safety.Hp > 0 &&
        (safety.Status & (TransformStatusMask | DeadStatusMask)) == 0 &&
        safety.Step != DeathStep;
}
