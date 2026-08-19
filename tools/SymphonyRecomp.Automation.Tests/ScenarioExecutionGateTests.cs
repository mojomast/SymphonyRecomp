using ModelContextProtocol;
using SymphonyRecomp.Mcp;
using SymphonyRecomp.Mcp.Scenarios;

namespace SymphonyRecomp.Automation.Tests;

public sealed class ScenarioExecutionGateTests
{
    [Fact]
    public void MutationFirstBlocksScenarioUntilRelease()
    {
        var gate = new ScenarioExecutionGate();
        IDisposable mutation = gate.TryEnterMutation();

        Assert.Throws<McpException>(() => gate.TryEnterScenario());

        mutation.Dispose();
        using IDisposable scenario = gate.TryEnterScenario();
    }

    [Fact]
    public void ScenarioFirstBlocksMutationUntilRelease()
    {
        var gate = new ScenarioExecutionGate();
        IDisposable scenario = gate.TryEnterScenario();

        Assert.Throws<McpException>(() => gate.TryEnterMutation());

        scenario.Dispose();
        using IDisposable mutation = gate.TryEnterMutation();
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task MutationLeaseReleasesExactlyOnceAfterAwaitedCompletion(string outcome)
    {
        var gate = new ScenarioExecutionGate();

        async Task Operation()
        {
            using IDisposable mutation = gate.TryEnterMutation();
            await (outcome switch
            {
                "success" => Task.CompletedTask,
                "failure" => Task.FromException(new IOException("failure")),
                _ => Task.FromCanceled(new CancellationToken(canceled: true)),
            });
        }

        if (outcome == "success") await Operation();
        else await Assert.ThrowsAnyAsync<Exception>(Operation);

        using IDisposable scenario = gate.TryEnterScenario();
        scenario.Dispose();
        scenario.Dispose();
        using IDisposable next = gate.TryEnterScenario();
    }

    [Fact]
    public void OrdinaryDirectMutationsMayOverlapButStillBlockScenario()
    {
        var gate = new ScenarioExecutionGate();
        using IDisposable first = gate.TryEnterMutation();
        using IDisposable second = gate.TryEnterMutation();

        Assert.Throws<McpException>(() => gate.TryEnterScenario());
    }

    [Fact]
    public void MutationLeaseDisposeIsIdempotent()
    {
        var gate = new ScenarioExecutionGate();
        IDisposable mutation = gate.TryEnterMutation();

        mutation.Dispose();
        mutation.Dispose();

        using IDisposable scenario = gate.TryEnterScenario();
    }

    [Fact]
    public async Task LaunchAndStopAreCoveredByScenarioLease()
    {
        var gate = new ScenarioExecutionGate();
        await using var process = new GameProcessManager();
        await using var client = new GameAutomationClient();
        var tools = new SotnTools(process, client, new ScenarioCatalog(), new UnusedExecutionService(), gate);
        using IDisposable scenario = gate.TryEnterScenario();

        await Assert.ThrowsAsync<McpException>(() => tools.LaunchGame(CancellationToken.None));
        await Assert.ThrowsAsync<McpException>(() => tools.StopGame(true, CancellationToken.None));
    }

    private sealed class UnusedExecutionService : IScenarioExecutionService
    {
        public Task<ScenarioExecutionResult> RunAsync(string source,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
