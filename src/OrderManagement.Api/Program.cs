using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using OrderManagement.Api.Persistence;
using OrderManagement.Api.Infrastructure;
using OrderManagement.Api.Orders;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
});
builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(
        new JsonStringEnumConverter(allowIntegerValues: false)))
    .ConfigureApiBehaviorOptions(options =>
    {
        options.SuppressMapClientErrors = true;
        options.InvalidModelStateResponseFactory = action =>
        {
            var problem = ApiProblems.Create(action.HttpContext, 400, "validation_error", "Periksa field pada request.");
            problem.Extensions["errors"] = action.ModelState
                .Where(entry => entry.Value?.Errors.Count > 0)
                .ToDictionary(entry => entry.Key, entry => entry.Value!.Errors.Select(error =>
                    string.IsNullOrEmpty(error.ErrorMessage) ? "Nilai tidak valid." : error.ErrorMessage).ToArray());
            var result = new BadRequestObjectResult(problem);
            result.ContentTypes.Add("application/problem+json");
            return result;
        };
    });
builder.Services.AddOpenApi();
builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<OrderStore>();

var app = builder.Build();
app.Services.GetRequiredService<Database>().Initialize();
app.UseMiddleware<RequestMiddleware>();
app.UseStatusCodePages(context => ApiProblems.Write(context.HttpContext, context.HttpContext.Response.StatusCode,
    $"http_{context.HttpContext.Response.StatusCode}", "Periksa URL, metode, dan content type request."));
app.MapControllers();
app.MapOpenApi();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.Run();

public partial class Program;
