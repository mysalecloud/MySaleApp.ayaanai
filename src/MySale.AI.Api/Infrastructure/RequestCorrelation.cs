using System.Text.RegularExpressions;
using MySale.AI.Application.ActivityTracking;

namespace MySale.AI.Api.Infrastructure;

/// <summary>
/// Gives every request one correlation id: taken from the X-Correlation-Id request header when it is well-formed,
/// otherwise generated. It becomes HttpContext.TraceIdentifier (so ProblemDetails "traceId" matches), is added to
/// every log line of the request, is returned in the X-Correlation-Id response header, is attached to the MongoDB
/// command and is stored in the activity log.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";
    public const string ItemKey = "ayaan.correlationId";
    private static readonly Regex Valid = new("^[A-Za-z0-9._-]{8,64}$", RegexOptions.Compiled);

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].ToString().Trim();
        var id = Valid.IsMatch(incoming) ? incoming : Guid.NewGuid().ToString("N");
        context.Items[ItemKey] = id;
        context.TraceIdentifier = id;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = id;
            return Task.CompletedTask;
        });
        using (_logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))
        {
            await _next(context);
        }
    }
}

/// <summary>Request context for activity tracking. Reads only non-secret headers (never Authorization).</summary>
public sealed class HttpRequestContext : IRequestContext
{
    private static readonly Regex Safe = new("^[A-Za-z0-9 ._:/+()-]{1,128}$", RegexOptions.Compiled);
    private readonly IHttpContextAccessor _accessor;
    private string? _fallback;

    public HttpRequestContext(IHttpContextAccessor accessor) => _accessor = accessor;

    public string CorrelationId
        => _accessor.HttpContext is { } http && http.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var id) && id is string s
            ? s
            : _fallback ??= Guid.NewGuid().ToString("N");

    public string? ClientApp => Header("X-Client-App");
    public string? ClientVersion => Header("X-Client-Version");
    public string? SessionId => Header("X-Session-Id");
    public string? Endpoint => _accessor.HttpContext?.Request.Path.Value;

    private string? Header(string name)
    {
        var value = _accessor.HttpContext?.Request.Headers[name].ToString().Trim();
        return !string.IsNullOrEmpty(value) && Safe.IsMatch(value) ? value : null;
    }
}
