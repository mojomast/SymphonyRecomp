using System.IO.Pipes;
using ModelContextProtocol;
using SymphonyRecomp.Automation.Contracts;
using SymphonyRecomp.Mcp;

namespace SymphonyRecomp.Automation.Tests;

public sealed class NamedPipeIntegrationTests
{
    [Fact]
    public async Task ClientAuthenticatesAndReadsTypedResponse()
    {
        string pipeName = $"sotn-test-{Guid.NewGuid():N}";
        const string token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        await using var client = new GameAutomationClient();
        client.Configure(pipeName, token, static () => true);
        Task server = ServeOneAsync(pipeName, request =>
        {
            Assert.Equal(token, request.Token);
            Assert.Equal("bridge.status", request.Method);
            var status = new BridgeStatusDto(AutomationProtocol.Version, true, 42, 7,
                "running", "Title", null, 0, false);
            return new AutomationResponse(request.Id, true,
                System.Text.Json.JsonSerializer.SerializeToElement(status, AutomationProtocol.Json));
        });

        BridgeStatusDto result = await client.GetBridgeStatusAsync(CancellationToken.None);
        await server;

        Assert.True(result.Ready);
        Assert.Equal(42, result.Frame);
    }

    [Fact]
    public async Task MismatchedResponseIdentifierFailsClosed()
    {
        string pipeName = $"sotn-test-{Guid.NewGuid():N}";
        await using var client = new GameAutomationClient();
        client.Configure(pipeName,
            "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
            static () => true);
        Task server = ServeOneAsync(pipeName, request =>
            new AutomationResponse("wrong-id", true,
                System.Text.Json.JsonSerializer.SerializeToElement(new { ok = true })));

        McpException error = await Assert.ThrowsAsync<McpException>(() =>
            client.GetBridgeStatusAsync(CancellationToken.None));
        await server;

        Assert.Contains("outcome may be unknown", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupQueueTimeoutIsRetriedUntilBridgeIsReady()
    {
        string pipeName = $"sotn-test-{Guid.NewGuid():N}";
        const string token = "123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0";
        await using var client = new GameAutomationClient();
        client.Configure(pipeName, token, static () => true);
        int requestCount = 0;
        Task server = ServeManyAsync(pipeName, 2, request =>
        {
            requestCount++;
            if (requestCount == 1)
                return new AutomationResponse(request.Id, false, Error:
                    new AutomationError("timeout", "Automation request timed out before execution."));

            var status = new BridgeStatusDto(AutomationProtocol.Version, true, 42, 7,
                "running", "Title", null, 0, false);
            return new AutomationResponse(request.Id, true,
                System.Text.Json.JsonSerializer.SerializeToElement(status, AutomationProtocol.Json));
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.WaitForBridgeReadyAsync(timeout.Token);
        await server;

        Assert.Equal(2, requestCount);
    }

    private static async Task ServeOneAsync(string pipeName, Func<AutomationRequest, AutomationResponse> respond)
    {
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await server.WaitForConnectionAsync();
        AutomationRequest request = await AutomationProtocol.ReadAsync<AutomationRequest>(server)
            ?? throw new InvalidDataException("Client closed without a request.");
        await AutomationProtocol.WriteAsync(server, respond(request));
    }

    private static async Task ServeManyAsync(string pipeName, int count,
        Func<AutomationRequest, AutomationResponse> respond)
    {
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await server.WaitForConnectionAsync();
        for (int i = 0; i < count; i++)
        {
            AutomationRequest request = await AutomationProtocol.ReadAsync<AutomationRequest>(server)
                ?? throw new InvalidDataException("Client closed without a request.");
            await AutomationProtocol.WriteAsync(server, respond(request));
        }
    }
}
