using System.Text.Json;
using System.Text.Json.Serialization;
using ExcelDatasetManager.Api.Models;

namespace ExcelDatasetManager.Api.Middleware;

/// <summary>
/// Catches any unhandled exception, logs it with full stack trace, and returns a structured
/// JSON error response so the caller (and browser dev tools) can see what actually went wrong.
/// Without this, ASP.NET returns a body-less 500 and the UI shows "Internal Server Error".
/// </summary>
public class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger,
    IHostEnvironment env)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Unhandled exception on {Method} {Path}",
                context.Request.Method, context.Request.Path);

            if (context.Response.HasStarted)
            {
                // Too late to rewrite the response — let Kestrel close the connection.
                throw;
            }

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json; charset=utf-8";

            // Always include the message + exception type so a self-hosted owner can debug
            // from the browser. Hide stack trace unless we're in Development.
            var payload = new
            {
                success = false,
                error = new
                {
                    code = ErrorCodes.Internal,
                    message = ex.Message,
                    exception_type = ex.GetType().FullName,
                    inner = ex.InnerException?.Message,
                    stack_trace = env.IsDevelopment() ? ex.StackTrace : null
                }
            };

            await context.Response.WriteAsync(JsonSerializer.Serialize(payload, JsonOptions));
        }
    }
}
