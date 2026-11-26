using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace OrderManagement.Api.Infrastructure;

public sealed class RequestMiddleware(RequestDelegate next, ILogger<RequestMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var supplied = context.Request.Headers["X-Correlation-ID"].ToString();
        var correlationId = supplied.Length is > 0 and <= 64 &&
            supplied.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            ? supplied : Guid.NewGuid().ToString("N");
        context.Items["CorrelationId"] = correlationId;
        context.Response.Headers["X-Correlation-ID"] = correlationId;

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["TraceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier
        });
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        catch (ApiException exception)
        {
            logger.LogWarning("Request rejected: {ErrorCode}", exception.Code);
            await ApiProblems.Write(context, exception.Status, exception.Code, exception.Message);
        }
        catch (BadHttpRequestException exception)
        {
            await ApiProblems.Write(context, exception.StatusCode, "invalid_request", "Periksa format request.");
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            logger.LogWarning(exception, "Database lock timeout");
            context.Response.Headers.RetryAfter = "1";
            await ApiProblems.Write(context, 503, "database_busy", "Silakan coba lagi dengan idempotency key yang sama.");
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            logger.LogInformation("Client disconnected");
            context.Response.StatusCode = 499;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unhandled request error");
            if (context.Response.HasStarted) throw;
            context.Response.Clear();
            context.Response.Headers["X-Correlation-ID"] = correlationId;
            await ApiProblems.Write(context, 500, "internal_error", "Request gagal diproses. Gunakan correlation ID untuk penelusuran.");
        }
        finally
        {
            logger.LogInformation("HTTP {Method} {Path} returned {StatusCode} in {ElapsedMs} ms",
                context.Request.Method, context.Request.Path.Value, context.Response.StatusCode,
                Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 2));
        }
    }
}
