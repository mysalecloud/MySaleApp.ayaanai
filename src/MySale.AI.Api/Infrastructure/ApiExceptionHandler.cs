using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using MySale.AI.Application.Abstractions;
using MySale.AI.Application.Services;

namespace MySale.AI.Api.Infrastructure;

/// <summary>Maps exceptions to ProblemDetails. Never exposes stack traces or internal details for 5xx errors.</summary>
public sealed class ApiExceptionHandler : IExceptionHandler
{
    private readonly ILogger<ApiExceptionHandler> _logger;
    private readonly IProblemDetailsService _problems;

    public ApiExceptionHandler(ILogger<ApiExceptionHandler> logger, IProblemDetailsService problems)
    {
        _logger = logger;
        _problems = problems;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, title, detail) = exception switch
        {
            NotFoundException e => (StatusCodes.Status404NotFound, "Not found", e.Message),
            AppValidationException e => (StatusCodes.Status400BadRequest, "Invalid request", e.Message),
            TenantResolutionException => (StatusCodes.Status400BadRequest, "Customer database not resolved", TenantResolutionException.UserMessage),
            AttachmentException e => (StatusCodes.Status400BadRequest, "Attachment rejected", e.Message),
            SpeechException e => (StatusCodes.Status502BadGateway, "Speech-to-text failed", e.Message),
            AIProviderException e => (StatusCodes.Status502BadGateway, "AI provider unavailable",
                context.User.IsInRole("Admin") ? e.Message : "AI service is currently unavailable."),
            QueryTimeoutException => (StatusCodes.Status504GatewayTimeout, "Query timeout", "The query took too long to run."),
            QueryExecutionException => (StatusCodes.Status502BadGateway, "Database error", "Unable to access your business data."),
            TimeoutException or MongoConnectionException => (StatusCodes.Status503ServiceUnavailable, "Database unavailable", "The database is not reachable. Please try again later."),
            OperationCanceledException => (499, "Request cancelled", "The request was cancelled."),
            _ => (StatusCodes.Status500InternalServerError, "Server error", "An unexpected error occurred.")
        };

        if (status >= 500) _logger.LogError(exception, "Unhandled exception for {Path}", context.Request.Path);
        else _logger.LogInformation("Request failed ({Status}): {Message}", status, exception.Message);

        if (context.Response.HasStarted) return true;
        context.Response.StatusCode = status;
        return await _problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = new ProblemDetails { Status = status, Title = title, Detail = detail }
        });
    }
}
