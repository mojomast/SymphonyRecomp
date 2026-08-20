using SymphonyRecomp.Automation;
using SymphonyRecomp.Mcp;
using RecompOne.Runtime.Events;
using SymphonyRecomp.Automation.Contracts;
using System.Text.Json;

namespace SymphonyRecomp.Automation.Tests;

public sealed class BridgeConstructionTests
{
    [Fact]
    public void BridgeRejectsWeakAttachToken()
    {
        Assert.Throws<ArgumentException>(() => new AutomationBridge($"sotn-test-{Guid.NewGuid():N}", "weak"));
    }

    [Fact]
    public void BridgeCanStartAndStopWithoutRuntimeContext()
    {
        using var bridge = new AutomationBridge(
            $"sotn-test-{Guid.NewGuid():N}",
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
    }

    [Fact]
    public async Task BridgeServesAuthenticatedStatusAtVSync()
    {
        string pipe = $"sotn-test-{Guid.NewGuid():N}";
        const string token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        using var bridge = new AutomationBridge(pipe, token);
        await using var client = new GameAutomationClient();
        client.Configure(pipe, token, static () => true);

        Task<SymphonyRecomp.Automation.Contracts.BridgeStatusDto> request =
            client.GetBridgeStatusAsync(CancellationToken.None);
        for (int frame = 1; frame <= 100 && !request.IsCompleted; frame++)
        {
            Event.Dispatch(new VSyncEvent { Frame = frame });
            await Task.Delay(5);
        }

        var status = await request;
        Assert.Equal(SymphonyRecomp.Automation.Contracts.AutomationProtocol.Version, status.ProtocolVersion);
        Assert.False(status.Ready);
    }

    [Fact]
    public void PendingCommandCancellationAndExecutionAreMutuallyExclusive()
    {
        var canceled = new AutomationBridge.PendingCommand(
            new AutomationBridge.AutomationCommand("a", "bridge.status", null, 1000));
        Assert.True(canceled.TryCancel());
        Assert.False(canceled.TryBeginExecution());

        var executing = new AutomationBridge.PendingCommand(
            new AutomationBridge.AutomationCommand("b", "bridge.status", null, 1000));
        Assert.True(executing.TryBeginExecution());
        Assert.False(executing.TryCancel());
    }

    [Fact]
    public async Task DisposingGameThreadCancelsDeferredMutation()
    {
        var gameThread = new AutomationGameThread(static () => 0, static () => 0);
        var pending = new AutomationBridge.PendingCommand(new AutomationBridge.AutomationCommand(
            "reset", "mods.diagnostics.reset",
            new ModDiagnosticsResetRequest("coop", new string('a', 32), 0, true), 1000));

        gameThread.Execute(pending);
        gameThread.Dispose();
        AutomationResponse response = await pending.Completion.Task;

        Assert.False(response.Success);
        Assert.Equal("shutdown", response.Error?.Code);
        Assert.False(pending.TryBeginExecution());
    }

    [Fact]
    public void DeferredCancellationDoesNotMisreportExecutingMutation()
    {
        var pending = new AutomationBridge.PendingCommand(new AutomationBridge.AutomationCommand(
            "reset", "mods.diagnostics.reset",
            new ModDiagnosticsResetRequest("coop", new string('a', 32), 0, true), 1000));
        Assert.True(pending.TryBeginExecution());

        AutomationGameThread.CancelDeferred(pending);

        Assert.False(pending.Completion.Task.IsCompleted);
    }

    [Fact]
    public void StructuredDiagnosticsRequireResetIdentityAndFinalBound()
    {
        string session = new('a', 32);
        string valid = $$"""{"sessionId":"{{session}}","generation":3,"schema":"p2d4/1"}""";

        ModDiagnosticsDto parsed = AutomationGameThread.ParseModDiagnostics("coop", 42, valid);

        Assert.Equal(session, parsed.SessionId);
        Assert.Equal(3, parsed.Generation);
        Assert.Equal(42, parsed.Frame);
        Assert.ThrowsAny<Exception>(() => AutomationGameThread.ParseModDiagnostics("coop", 42, "{}"));

        string expanding = $$"""{"sessionId":"{{session}}","generation":3,"data":"{{new string('é', 11_000)}}"}""";
        Assert.ThrowsAny<Exception>(() => AutomationGameThread.ParseModDiagnostics("coop", 42, expanding));
    }

    [Fact]
    public async Task AtomicInputBatchInstallsBothPortsAtOneFrameAndClearIsNeutral()
    {
        using var gameThread = new AutomationGameThread(static () => 0, static () => 0);
        var request = new InputBatchRequest([
            new InputTimelineRequest(0, [new InputSegmentDto(0x2000, 3)]),
            new InputTimelineRequest(1, [new InputSegmentDto(0x0040, 5)])]);
        var pending = new AutomationBridge.PendingCommand(new AutomationBridge.AutomationCommand(
            "batch", "input.batch", request, 1000));

        gameThread.Execute(pending);
        AutomationResponse response = await pending.Completion.Task;
        InputBatchOperationDto batch = response.Result!.Value.Deserialize<InputBatchOperationDto>(AutomationProtocol.Json)!;

        Assert.True(response.Success);
        Assert.Equal(2, batch.Operations.Length);
        Assert.All(batch.Operations, operation => Assert.Equal(batch.StartsAfterFrame, operation.StartsAfterFrame));
        Assert.Equal([(0x2000, 3), (0x0040, 5)], gameThread.InputSnapshotForTests()
            .Select(value => ((int)value.Mask, value.RemainingFrames)));

        var clear = new AutomationBridge.PendingCommand(new AutomationBridge.AutomationCommand(
            "clear", "input.clear", null, 1000));
        gameThread.Execute(clear);
        Assert.True((await clear.Completion.Task).Success);
        Assert.All(gameThread.InputSnapshotForTests(), value => Assert.Equal((0, 0), ((int)value.Mask, value.RemainingFrames)));
    }

    [Fact]
    public void InputBatchValidationIsAllOrNothingClosedAndBounded()
    {
        JsonElement valid = JsonSerializer.SerializeToElement(new InputBatchRequest([
            new InputTimelineRequest(0, [new InputSegmentDto(1, 1800)]),
            new InputTimelineRequest(1, [new InputSegmentDto(2, 1)])]), AutomationProtocol.Json);
        InputBatchRequest parsed = AutomationBridge.ValidateInputBatchForTests(valid);
        Assert.Equal(2, parsed.Timelines.Length);

        JsonElement duplicate = JsonSerializer.SerializeToElement(new InputBatchRequest([
            new InputTimelineRequest(0, [new InputSegmentDto(1, 1)]),
            new InputTimelineRequest(0, [new InputSegmentDto(2, 1)])]), AutomationProtocol.Json);
        Assert.ThrowsAny<Exception>(() => AutomationBridge.ValidateInputBatchForTests(duplicate));

        JsonElement invalidSecond = JsonSerializer.SerializeToElement(new InputBatchRequest([
            new InputTimelineRequest(0, [new InputSegmentDto(1, 1)]),
            new InputTimelineRequest(1, [new InputSegmentDto(2, 1801)])]), AutomationProtocol.Json);
        Assert.ThrowsAny<Exception>(() => AutomationBridge.ValidateInputBatchForTests(invalidSecond));

        using JsonDocument unknownDocument = JsonDocument.Parse("""
            {"timelines":[{"port":0,"segments":[{"buttons":0,"frames":1}],"extra":true}]}
            """);
        Assert.ThrowsAny<Exception>(() => AutomationBridge.ValidateInputBatchForTests(unknownDocument.RootElement));
    }
}
