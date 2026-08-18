using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SymphonyRecomp.Mcp;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.Services.AddSingleton<GameProcessManager>();
builder.Services.AddSingleton<GameAutomationClient>();
var toolJson = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
};
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<SotnTools>(toolJson);

await builder.Build().RunAsync();
