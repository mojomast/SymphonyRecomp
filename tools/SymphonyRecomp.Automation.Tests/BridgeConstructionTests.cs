using SymphonyRecomp.Automation;
using SymphonyRecomp.Mcp;
using RecompOne.Runtime.Events;

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
}
