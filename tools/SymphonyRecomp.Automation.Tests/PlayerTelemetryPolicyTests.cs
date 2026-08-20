using SymphonyRecomp.Automation;
using SymphonyRecomp.Automation.Contracts;

namespace SymphonyRecomp.Automation.Tests;

public sealed class PlayerTelemetryPolicyTests
{
    [Fact]
    public void LiveAlucardProducesTelemetryWithoutEntityUpdatePrerequisite()
    {
        const uint genericEntityUpdate = 0;
        PlayerTelemetryDto expected = Telemetry();

        PlayerTelemetryDto? actual = PlayerTelemetryPolicy.Capture(Live(), () => expected);

        Assert.Equal(0u, genericEntityUpdate);
        Assert.Same(expected, actual);
    }

    [Theory]
    [InlineData(0, 0u, 0x00)]
    [InlineData(10, PlayerTelemetryPolicy.DeadStatusMask, 0x00)]
    [InlineData(10, 0u, PlayerTelemetryPolicy.DeathStep)]
    public void DeadHpStatusOrNativeStepSuppressesTelemetry(int hp, uint status, ushort step)
    {
        Assert.Null(PlayerTelemetryPolicy.Capture(Live() with { Hp = hp, Status = status, Step = step }, Telemetry));
    }

    [Fact]
    public void NonPlaySuppressesTelemetry()
    {
        Assert.Null(PlayerTelemetryPolicy.Capture(Live() with { IsPlay = false }, Telemetry));
    }

    [Fact]
    public void UnavailableOrFaultingMemorySuppressesTelemetry()
    {
        Assert.Null(PlayerTelemetryPolicy.Capture(Live() with { MemoryAvailable = false }, Telemetry));
        Assert.Null(PlayerTelemetryPolicy.Capture(Live(), static () => throw new InvalidOperationException("memory")));
    }

    [Theory]
    [InlineData("loading")]
    [InlineData("transition")]
    [InlineData("control")]
    [InlineData("transform")]
    public void UnsafePlayStateSuppressesTelemetry(string condition)
    {
        PlayerTelemetrySafety state = condition switch
        {
            "loading" => Live() with { IsLoading = true },
            "transition" => Live() with { SpecialTransitionClear = false },
            "control" => Live() with { HasControl = false },
            "transform" => Live() with { Status = PlayerTelemetryPolicy.TransformStatusMask },
            _ => throw new ArgumentOutOfRangeException(nameof(condition)),
        };
        Assert.Null(PlayerTelemetryPolicy.Capture(state, Telemetry));
    }

    static PlayerTelemetrySafety Live() => new(
        true, true, true, false, false, false, true, true, true, true, true, 10, 0, 0);

    static PlayerTelemetryDto Telemetry() => new(
        1, 2, 3, 4, 5, 6, false, "Standing", 0, true, false,
        10, 20, 3, 4, 5, 6, 7, 8, 9, 10);
}
