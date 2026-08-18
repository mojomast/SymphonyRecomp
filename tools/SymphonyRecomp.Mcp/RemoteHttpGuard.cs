using Microsoft.AspNetCore.Http;

namespace SymphonyRecomp.Mcp;

internal sealed record RemoteHttpOptions(string Host, int Port)
{
    public static RemoteHttpOptions FromEnvironment()
    {
        string host = (Environment.GetEnvironmentVariable("SYMPHONYRECOMP_MCP_HTTP_HOST") ?? "").Trim();
        if (Uri.CheckHostName(host) != UriHostNameType.Dns || host.EndsWith(".", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "SYMPHONYRECOMP_MCP_HTTP_HOST must be the exact Tailscale HTTPS DNS name.");

        string? rawPort = Environment.GetEnvironmentVariable("SYMPHONYRECOMP_MCP_HTTP_PORT");
        int port = string.IsNullOrWhiteSpace(rawPort) ? 8765 :
            int.TryParse(rawPort, out int parsed) && parsed is > 0 and <= 65535 ? parsed :
            throw new InvalidOperationException("SYMPHONYRECOMP_MCP_HTTP_PORT must be between 1 and 65535.");
        return new RemoteHttpOptions(host, port);
    }
}

internal sealed class RemoteHttpGuard(RequestDelegate next, RemoteHttpOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Append("X-Accel-Buffering", "no");

        if (context.Request.Path != "/mcp" || context.Request.QueryString.HasValue)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.Headers.Allow = "POST";
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }
        if (!ValidHost(context.Request.Host))
        {
            context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            return;
        }
        if (!ValidOrigin(context.Request.Headers.Origin))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        if (context.Request.ContentType is not { } contentType ||
            !string.Equals(contentType.Split(';', 2)[0].Trim(), "application/json",
                StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return;
        }

        await next(context);
    }

    internal bool ValidHost(HostString host) =>
        string.Equals(host.Host, options.Host, StringComparison.OrdinalIgnoreCase) &&
        (host.Port is null or 443 || host.Port == options.Port);

    internal bool ValidOrigin(Microsoft.Extensions.Primitives.StringValues values)
    {
        if (values.Count == 0) return true;
        if (values.Count != 1 || values[0] is not { } value || value.Contains(',')) return false;
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? origin) &&
            origin.Scheme == Uri.UriSchemeHttps &&
            string.Equals(origin.Host, options.Host, StringComparison.OrdinalIgnoreCase) &&
            origin.IsDefaultPort && origin.AbsolutePath == "/" &&
            string.IsNullOrEmpty(origin.Query) && string.IsNullOrEmpty(origin.Fragment) &&
            string.IsNullOrEmpty(origin.UserInfo);
    }
}
