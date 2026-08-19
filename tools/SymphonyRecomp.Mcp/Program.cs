using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using SymphonyRecomp.Mcp;
using SymphonyRecomp.Mcp.Scenarios;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

bool http = args.Any(arg => string.Equals(arg, "--http", StringComparison.Ordinal));
string[] hostArgs = args.Where(arg => !string.Equals(arg, "--http", StringComparison.Ordinal)).ToArray();

if (!http)
{
    var builder = Host.CreateApplicationBuilder(hostArgs);
    ConfigureLogging(builder.Logging);
    AddMcpServices(builder.Services).WithStdioServerTransport();

    IHost host = builder.Build();
    try { await host.RunAsync(); }
    finally { await ((IAsyncDisposable)host).DisposeAsync(); }
    return;
}

RemoteHttpOptions remote = RemoteHttpOptions.FromEnvironment();
var webBuilder = WebApplication.CreateBuilder(hostArgs);
ConfigureLogging(webBuilder.Logging);
webBuilder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, remote.Port);
    options.Limits.MaxRequestBodySize = 1024 * 1024;
    options.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
    options.Limits.MaxRequestHeaderCount = 32;
    options.Limits.MaxRequestLineSize = 8 * 1024;
    options.Limits.MaxConcurrentConnections = 16;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
});
webBuilder.Services.AddSingleton(remote);
webBuilder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(45));
AddMcpServices(webBuilder.Services)
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless);

await using WebApplication app = webBuilder.Build();
app.UseMiddleware<RemoteHttpGuard>();
app.MapMcp("/mcp");
await app.RunAsync();

static IMcpServerBuilder AddMcpServices(IServiceCollection services)
{
    services.AddSingleton<GameProcessManager>();
    services.AddSingleton<GameAutomationClient>();
    services.AddSingleton<ScenarioAutomationClient>();
    services.AddSingleton<IScenarioAutomationClient>(provider =>
        provider.GetRequiredService<ScenarioAutomationClient>());
    services.AddSingleton<IScenarioClock, SystemScenarioClock>();
    services.AddSingleton<ScenarioCatalog>();
    services.AddSingleton<ScenarioExecutionGate>();
    services.AddSingleton<ScenarioExecutionService>();
    services.AddSingleton<IScenarioExecutionService>(provider =>
        provider.GetRequiredService<ScenarioExecutionService>());
    var toolJson = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };
    return services.AddMcpServer().WithTools<SotnTools>(toolJson);
}

static void ConfigureLogging(ILoggingBuilder logging)
{
    logging.ClearProviders();
    logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
}
