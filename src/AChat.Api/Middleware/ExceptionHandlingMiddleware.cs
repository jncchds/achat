using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AChat.Api.Middleware;

public partial class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            LogUnhandledException(logger, context.Request.Path, ex);
            await WriteErrorResponseAsync(context, ex);
        }
    }

    private static async Task WriteErrorResponseAsync(HttpContext context, Exception ex)
    {
        context.Response.ContentType = "application/json";

        var (statusCode, message) = ex switch
        {
            UnauthorizedAccessException => (HttpStatusCode.Unauthorized, "Unauthorized"),
            KeyNotFoundException => (HttpStatusCode.NotFound, "Resource not found"),
            ArgumentException arg => (HttpStatusCode.BadRequest, arg.Message),
            InvalidOperationException inv => (HttpStatusCode.BadRequest, inv.Message),
            _ => (HttpStatusCode.InternalServerError, "An unexpected error occurred")
        };

        context.Response.StatusCode = (int)statusCode;
        await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = message }));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception on {Path}")]
    private static partial void LogUnhandledException(ILogger logger, string path, Exception ex);
}
