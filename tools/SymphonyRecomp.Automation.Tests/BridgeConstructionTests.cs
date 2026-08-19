using SymphonyRecomp.Automation;
using SymphonyRecomp.Mcp;
using RecompOne.Runtime.Events;
using SymphonyRecomp.Automation.Contracts;

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
}
