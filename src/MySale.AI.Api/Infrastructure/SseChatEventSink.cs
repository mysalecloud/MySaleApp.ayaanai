using System.Text.Json;
using MySale.AI.Application.Agent;
using MySale.AI.Application.Contracts;

namespace MySale.AI.Api.Infrastructure;

/// <summary>Writes orchestrator progress as Server-Sent Events (text/event-stream).</summary>
public sealed class SseChatEventSink : IChatEventSink
{
    private readonly HttpResponse _response;
    private readonly JsonSerializerOptions _json;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SseChatEventSink(HttpResponse response, JsonSerializerOptions json)
    {
        _response = response;
        _json = json;
    }

    public bool StreamTokens => true;

    public static void Prepare(HttpResponse response)
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache, no-transform";
        response.Headers["X-Accel-Buffering"] = "no"; // nginx: don't buffer SSE
        response.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
    }

    public Task OnStatusAsync(string stage, string message, CancellationToken ct) => WriteAsync("status", new { stage, message }, ct);

    public Task OnQueryAsync(QueryInfoDto query, CancellationToken ct) => WriteAsync("query", new { query }, ct);

    public Task OnTokenAsync(string text, CancellationToken ct) => WriteAsync("token", new { text }, ct);

    public async Task WriteAsync(string eventName, object payload, CancellationToken ct)
    {
        var data = JsonSerializer.Serialize(payload, _json);
        await _lock.WaitAsync(ct);
        try
        {
            await _response.WriteAsync($"event: {eventName}\ndata: {data}\n\n", ct);
            await _response.Body.FlushAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Client disconnected; the orchestrator keeps going and persists the turn.
        }
        finally
        {
            _lock.Release();
        }
    }
}
