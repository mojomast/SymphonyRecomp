using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using SymphonyRecomp.Mcp;

namespace SymphonyRecomp.Automation.Tests;

public sealed class RemoteHttpGuardTests
{
    const string Host = "sotn-windows.example-tailnet.ts.net";

    [Fact]
    public void HostAndOriginAreExact()
    {
        var guard = Guard();
        Assert.True(guard.ValidHost(new HostString(Host)));
        Assert.True(guard.ValidHost(new HostString(Host, 443)));
        Assert.True(guard.ValidHost(new HostString(Host, 8765)));
        Assert.False(guard.ValidHost(new HostString("localhost", 8765)));
        Assert.False(guard.ValidHost(new HostString($"{Host}.evil.example")));
        Assert.False(guard.ValidHost(new HostString(Host, 8443)));

        Assert.True(guard.ValidOrigin(StringValues.Empty));
        Assert.True(guard.ValidOrigin(new StringValues($"https://{Host}")));
        Assert.False(guard.ValidOrigin(new StringValues("null")));
        Assert.False(guard.ValidOrigin(new StringValues($"http://{Host}")));
        Assert.False(guard.ValidOrigin(new StringValues($"https://{Host}.evil.example")));
        Assert.False(guard.ValidOrigin(new StringValues(["https://one.example", "https://two.example"])));
    }

    [Fact]
    public async Task ValidPostReachesMcpPipeline()
    {
        bool called = false;
        var guard = new RemoteHttpGuard(_ => { called = true; return Task.CompletedTask; },
            new RemoteHttpOptions(Host, 8765));
        DefaultHttpContext context = Context("POST", Host);

        await guard.InvokeAsync(context);

        Assert.True(called);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
    }

    [Theory]
    [InlineData("GET", Host, 405)]
    [InlineData("POST", "localhost", 421)]
    public async Task InvalidMethodOrHostIsRejected(string method, string host, int status)
    {
        bool called = false;
        var guard = new RemoteHttpGuard(_ => { called = true; return Task.CompletedTask; },
            new RemoteHttpOptions(Host, 8765));
        DefaultHttpContext context = Context(method, host);

        await guard.InvokeAsync(context);

        Assert.False(called);
        Assert.Equal(status, context.Response.StatusCode);
    }

    [Fact]
    public async Task BrowserOriginAndQueryStringFailClosed()
    {
        var guard = Guard();
        DefaultHttpContext origin = Context("POST", Host);
        origin.Request.Headers.Origin = "https://evil.example";
        await guard.InvokeAsync(origin);
        Assert.Equal(403, origin.Response.StatusCode);

        DefaultHttpContext query = Context("POST", Host);
        query.Request.QueryString = new QueryString("?token=nope");
        await guard.InvokeAsync(query);
        Assert.Equal(404, query.Response.StatusCode);
    }

    static RemoteHttpGuard Guard() => new(_ => Task.CompletedTask, new RemoteHttpOptions(Host, 8765));

    static DefaultHttpContext Context(string method, string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = "/mcp";
        context.Request.Host = new HostString(host);
        context.Request.ContentType = "application/json";
        return context;
    }
}
