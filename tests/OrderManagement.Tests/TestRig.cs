using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using OrderManagement.Api.Contracts;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace OrderManagement.Tests;

internal sealed class ApiFactory(string databasePath) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Database:Path"] = databasePath,
                ["Database:TimeoutSeconds"] = "15",
                ["Logging:LogLevel:Default"] = "None"
            }));
    }
}

internal sealed class TestRig : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "order-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ApiFactory _first;
    private readonly ApiFactory _second;
    public string DatabasePath { get; }
    public HttpClient First { get; }
    public HttpClient Second { get; }

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public TestRig()
    {
        Directory.CreateDirectory(_directory);
        DatabasePath = Path.Combine(_directory, "orders.db");
        _first = new(DatabasePath);
        _second = new(DatabasePath);
        First = _first.CreateClient();
        Second = _second.CreateClient();
    }

    public long Scalar(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False;Foreign Keys=True");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public long Stock(int id = 1) => Scalar($"SELECT StockQuantity FROM Products WHERE Id = {id};");

    public static object Payload(int quantity = 2, Guid? customer = null) => new
    {
        customerId = customer ?? Guid.Parse("11111111-1111-1111-1111-111111111111"),
        shippingAddress = "Jl. Melati No. 10, Bandung",
        items = new[] { new { productId = 1, quantity } }
    };

    public static async Task<HttpResponseMessage> Create(HttpClient client, object payload, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = JsonContent.Create(payload)
        };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    public static async Task<OrderResponse> ReadOrder(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OrderResponse>(Json))!;
    }

    public static async Task<HttpResponseMessage[]> Race(params Func<Task<HttpResponseMessage>>[] actions)
    {
        using var ready = new CountdownEvent(actions.Length);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = actions.Select(action => Task.Run(async () =>
        {
            ready.Signal();
            await gate.Task;
            return await action();
        })).ToArray();
        var allReady = ready.Wait(TimeSpan.FromSeconds(15));
        gate.SetResult();
        Assert.True(allReady, "Concurrent requests did not reach the start barrier.");
        return await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60));
    }

    public void Dispose()
    {
        First.Dispose();
        Second.Dispose();
        _first.Dispose();
        _second.Dispose();
        SqliteConnection.ClearAllPools();
        var parent = Path.GetDirectoryName(Path.GetFullPath(_directory));
        if (string.Equals(parent, Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(_directory).StartsWith("order-tests-", StringComparison.Ordinal))
            Directory.Delete(_directory, recursive: true);
    }
}
