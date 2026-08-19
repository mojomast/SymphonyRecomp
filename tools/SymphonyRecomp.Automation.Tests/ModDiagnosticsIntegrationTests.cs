using System.IO.Pipes;
using System.Text.Json;
using SymphonyRecomp.Automation.Contracts;
using SymphonyRecomp.Mcp;

namespace SymphonyRecomp.Automation.Tests;

public sealed class ModDiagnosticsIntegrationTests
{
    [Fact]
    public async Task ClientNegotiatesAndReturnsStructuredDiagnostics()
    {
        string pipeName = $"sotn-test-{Guid.NewGuid():N}";
        const string token = "123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0";
        await using var client = new GameAutomationClient();
        client.Configure(pipeName, token, static () => true);
        Task server = ServeAsync(pipeName, request => request.Method switch
        {
            "bridge.status" => Success(request.Id, new BridgeStatusDto(
                AutomationProtocol.Version, true, 10, 1, "running", "Play", "CEN", 0, false)),
            "mods.diagnostics.capture" => Success(request.Id, new ModDiagnosticsDto(
                "coop", 11, new string('a', 32), 3, JsonSerializer.SerializeToElement(new
                {
                    schema = "p2d4/1", sessionId = new string('a', 32), generation = 3
                }))),
            _ => throw new InvalidOperationException(request.Method),
        });

        ModDiagnosticsDto result = await client.CaptureModDiagnosticsAsync(
            new ModDiagnosticsCaptureRequest("coop"), CancellationToken.None);
        await server;

        Assert.Equal("coop", result.Id);
        Assert.Equal(11, result.Frame);
        Assert.Equal("p2d4/1", result.Payload.GetProperty("schema").GetString());
        Assert.Equal(3, result.Payload.GetProperty("generation").GetInt32());
    }

    [Fact]
    public async Task ClientSendsGenerationCheckedResetIdentity()
    {
        string pipeName = $"sotn-test-{Guid.NewGuid():N}";
        const string token = "123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0";
        string session = new('a', 32);
        ModDiagnosticsResetRequest? observed = null;
        await using var client = new GameAutomationClient();
        client.Configure(pipeName, token, static () => true);
        Task server = ServeAsync(pipeName, request =>
        {
            if (request.Method == "bridge.status")
                return Success(request.Id, new BridgeStatusDto(
                    AutomationProtocol.Version, true, 10, 1, "running", "Play", "CEN", 0, false));
            observed = request.Parameters!.Value.Deserialize<ModDiagnosticsResetRequest>(AutomationProtocol.Json);
            return Success(request.Id, new ModDiagnosticsResetDto("coop", 12, true));
        });

        await client.ResetModDiagnosticsAsync(
            new ModDiagnosticsResetRequest("coop", session, 3, true), CancellationToken.None);
        await server;

        Assert.Equal(new ModDiagnosticsResetRequest("coop", session, 3, true), observed);
    }

    static async Task ServeAsync(string pipeName, Func<AutomationRequest, AutomationResponse> respond)
    {
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await server.WaitForConnectionAsync();
        for (int i = 0; i < 2; i++)
        {
            AutomationRequest request = await AutomationProtocol.ReadAsync<AutomationRequest>(server)
                ?? throw new InvalidDataException("Client closed without a request.");
            await AutomationProtocol.WriteAsync(server, respond(request));
        }
    }

    static AutomationResponse Success<T>(string id, T result) =>
        new(id, true, JsonSerializer.SerializeToElement(result, AutomationProtocol.Json));
}
