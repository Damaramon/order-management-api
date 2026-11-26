using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace OrderManagement.Api.Infrastructure;

public static class ApiProblems
{
    public static ProblemDetails Create(HttpContext context, int status, string code, string detail)
    {
        var problem = new ProblemDetails
        {
            Type = $"urn:order-management:error:{code}",
            Title = status switch
            {
                400 => "Request tidak valid",
                404 => "Data tidak ditemukan",
                405 => "Metode tidak diizinkan",
                409 => "Konflik data",
                415 => "Content type tidak didukung",
                422 => "Operasi tidak dapat diproses",
                503 => "Database sedang sibuk",
                _ => "Terjadi kesalahan"
            },
            Status = status,
            Detail = detail,
            Instance = context.Request.Path
        };
        problem.Extensions["code"] = code;
        problem.Extensions["correlationId"] = context.Items["CorrelationId"]?.ToString();
        problem.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        return problem;
    }

    public static Task Write(HttpContext context, int status, string code, string detail)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(
            Create(context, status, code, detail), options: null,
            contentType: "application/problem+json", cancellationToken: CancellationToken.None);
    }
}
