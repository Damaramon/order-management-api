using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using OrderManagement.Api.Contracts;
using Xunit;

namespace OrderManagement.Tests;

public sealed class OrderApiTests
{
    [Fact]
    public async Task Create_saves_price_snapshot_and_get_returns_version()
    {
        using var rig = new TestRig();
        var response = await TestRig.Create(rig.First, TestRig.Payload(), "create");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await TestRig.ReadOrder(response);
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Equal(1, order.Version);
        Assert.Equal(150000, order.Total);
        Assert.Equal(75000, order.Items.Single().UnitPrice);
        Assert.Equal($"/orders/{order.Id}", response.Headers.Location!.ToString());
        var fetched = await rig.First.GetAsync(response.Headers.Location);
        var saved = await TestRig.ReadOrder(fetched);
        Assert.Equal(order.Id, saved.Id);
        Assert.Equal("\"1\"", fetched.Headers.ETag!.Tag);
        Assert.Equal(13, rig.Stock());
    }

    [Fact]
    public async Task Replay_returns_original_response_after_status_changes_and_new_host_start()
    {
        using var rig = new TestRig();
        var first = await TestRig.Create(rig.First, TestRig.Payload(), "durable-key");
        var originalBody = await first.Content.ReadAsStringAsync();
        var order = await TestRig.ReadOrder(first);
        var update = await rig.First.PatchAsJsonAsync($"/orders/{order.Id}/status",
            new { status = "Confirmed", expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        using var freshHost = new ApiFactory(rig.DatabasePath);
        using var freshClient = freshHost.CreateClient();
        var replay = await TestRig.Create(freshClient, TestRig.Payload(), "durable-key");
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(originalBody, await replay.Content.ReadAsStringAsync());
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(OrderStatus.Confirmed, (await TestRig.ReadOrder(await freshClient.GetAsync($"/orders/{order.Id}"))).Status);
        Assert.Equal(13, rig.Stock());
    }

    [Theory]
    [InlineData(2, 26, 409)]
    [InlineData(999, 1, 404)]
    public async Task Failure_on_later_item_rolls_back_order_stock_and_key(int secondProduct, int quantity, int expectedStatus)
    {
        using var rig = new TestRig();
        var response = await TestRig.Create(rig.First, new
        {
            customerId = Guid.NewGuid(),
            shippingAddress = "Bandung",
            items = new[] { new { productId = 1, quantity = 3 }, new { productId = secondProduct, quantity } }
        }, "rollback");
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Equal(15, rig.Stock());
        Assert.Equal(25, rig.Stock(2));
        Assert.Equal(0, rig.Scalar("SELECT COUNT(*) FROM Orders;"));
        Assert.Equal(0, rig.Scalar("SELECT COUNT(*) FROM OrderItems;"));
        Assert.Equal(0, rig.Scalar("SELECT COUNT(*) FROM IdempotencyRequests;"));
        Assert.Equal(HttpStatusCode.Created, (await TestRig.Create(rig.First, TestRig.Payload(), "rollback")).StatusCode);
    }

    [Fact]
    public async Task Duplicate_product_lines_are_merged_and_reordered_payload_replays()
    {
        using var rig = new TestRig();
        var customer = Guid.NewGuid();
        var first = await TestRig.Create(rig.First, new
        {
            customerId = customer,
            shippingAddress = "  Bandung  ",
            items = new[] { new { productId = 2, quantity = 1 }, new { productId = 1, quantity = 2 }, new { productId = 1, quantity = 3 } }
        }, "normalized");
        var replay = await TestRig.Create(rig.Second, new
        {
            customerId = customer,
            shippingAddress = "Bandung",
            items = new[] { new { productId = 1, quantity = 5 }, new { productId = 2, quantity = 1 } }
        }, "normalized");
        Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        var order = await TestRig.ReadOrder(first);
        Assert.Equal(2, order.Items.Count);
        Assert.Equal(5, order.Items.Single(item => item.ProductId == 1).Quantity);
        Assert.Equal(10, rig.Stock());
    }

    [Fact]
    public async Task Valid_delivery_flow_rejects_changes_from_terminal_state()
    {
        using var rig = new TestRig();
        var order = await TestRig.ReadOrder(await TestRig.Create(rig.First, TestRig.Payload(), "delivery"));
        foreach (var status in new[] { "Confirmed", "Shipped", "Delivered" })
            order = await TestRig.ReadOrder(await rig.First.PatchAsJsonAsync($"/orders/{order.Id}/status",
                new { status, expectedVersion = order.Version }));
        Assert.Equal(4, order.Version);
        var invalid = await rig.First.PostAsJsonAsync($"/orders/{order.Id}/cancel", new { expectedVersion = 4 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal(13, rig.Stock());
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Shipped")]
    [InlineData("Delivered")]
    public async Task Invalid_pending_transition_does_not_change_state_or_stock(string target)
    {
        using var rig = new TestRig();
        var order = await TestRig.ReadOrder(await TestRig.Create(rig.First, TestRig.Payload(), "invalid-transition"));
        var response = await rig.First.PatchAsJsonAsync($"/orders/{order.Id}/status", new { status = target, expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var saved = await TestRig.ReadOrder(await rig.First.GetAsync($"/orders/{order.Id}"));
        Assert.Equal(OrderStatus.Pending, saved.Status);
        Assert.Equal(1, saved.Version);
        Assert.Equal(13, rig.Stock());
    }

    [Fact]
    public async Task Filters_and_pagination_return_matching_orders_and_total()
    {
        using var rig = new TestRig();
        var customer = Guid.NewGuid();
        var a = await TestRig.ReadOrder(await TestRig.Create(rig.First, TestRig.Payload(1, customer), "list-a"));
        await TestRig.Create(rig.First, TestRig.Payload(1, customer), "list-b");
        await TestRig.Create(rig.First, TestRig.Payload(1, Guid.NewGuid()), "list-c");
        await rig.First.PatchAsJsonAsync($"/orders/{a.Id}/status", new { status = "Confirmed", expectedVersion = 1 });
        var from = Uri.EscapeDataString(a.CreatedAt.AddMinutes(-1).ToString("O"));
        var to = Uri.EscapeDataString(a.CreatedAt.AddMinutes(1).ToString("O"));
        var page = (await rig.First.GetFromJsonAsync<PagedOrders>(
            $"/orders?customerId={customer}&dateFrom={from}&dateTo={to}&page=1&pageSize=1", TestRig.Json))!;
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(2, page.TotalPages);
        Assert.Single(page.Items);
        var confirmed = (await rig.First.GetFromJsonAsync<PagedOrders>($"/orders?status=Confirmed&customerId={customer}", TestRig.Json))!;
        Assert.Equal(a.Id, Assert.Single(confirmed.Items).Id);
        var empty = (await rig.First.GetFromJsonAsync<PagedOrders>("/orders?page=100", TestRig.Json))!;
        Assert.Empty(empty.Items);
        Assert.Equal(3, empty.TotalCount);
    }

    [Theory]
    [InlineData("{\"shippingAddress\":\"Bandung\",\"items\":[{\"productId\":1,\"quantity\":1}]}")]
    [InlineData("{\"customerId\":\"11111111-1111-1111-1111-111111111111\",\"shippingAddress\":\"   \",\"items\":[{\"productId\":1,\"quantity\":1}]}")]
    [InlineData("{\"customerId\":\"11111111-1111-1111-1111-111111111111\",\"shippingAddress\":\"Bandung\",\"items\":[]}")]
    [InlineData("{\"customerId\":\"11111111-1111-1111-1111-111111111111\",\"shippingAddress\":\"Bandung\",\"items\":[null]}")]
    [InlineData("{\"customerId\":\"11111111-1111-1111-1111-111111111111\",\"shippingAddress\":\"Bandung\",\"items\":[{\"productId\":1,\"quantity\":-1}]}")]
    [InlineData("{\"customerId\":\"11111111-1111-1111-1111-111111111111\",\"shippingAddress\":\"Bandung\",\"items\":null}")]
    [InlineData("{broken-json")]
    public async Task Invalid_payload_returns_problem_details_without_writes(string json)
    {
        using var rig = new TestRig();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", "invalid");
        var response = await rig.First.SendAsync(request);
        await AssertProblem(response, 400);
        Assert.Equal(0, rig.Scalar("SELECT COUNT(*) FROM Orders;"));
        Assert.Equal(15, rig.Stock());
    }

    [Fact]
    public async Task Missing_key_and_missing_expected_version_are_rejected()
    {
        using var rig = new TestRig();
        await AssertProblem(await TestRig.Create(rig.First, TestRig.Payload()), 400);
        var order = await TestRig.ReadOrder(await TestRig.Create(rig.First, TestRig.Payload(), "version-required"));
        await AssertProblem(await rig.First.PatchAsJsonAsync($"/orders/{order.Id}/status", new { status = "Confirmed" }), 400);
        await AssertProblem(await rig.First.PostAsJsonAsync($"/orders/{order.Id}/cancel", new { }), 400);
    }

    [Theory]
    [InlineData("/orders?page=0")]
    [InlineData("/orders?pageSize=101")]
    [InlineData("/orders?status=Unknown")]
    [InlineData("/orders?dateFrom=2026-12-01&dateTo=2026-01-01")]
    public async Task Invalid_filters_return_consistent_errors(string url)
    {
        using var rig = new TestRig();
        await AssertProblem(await rig.First.GetAsync(url), 400);
    }

    [Fact]
    public async Task Correlation_id_is_echoed_on_success_and_errors()
    {
        using var rig = new TestRig();
        rig.First.DefaultRequestHeaders.Add("X-Correlation-ID", "test-trace-123");
        var success = await rig.First.GetAsync("/products");
        Assert.Equal("test-trace-123", success.Headers.GetValues("X-Correlation-ID").Single());
        var error = await rig.First.GetAsync($"/orders/{Guid.NewGuid()}");
        await AssertProblem(error, 404);
        using var body = JsonDocument.Parse(await error.Content.ReadAsStringAsync());
        Assert.Equal("test-trace-123", body.RootElement.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task Unknown_routes_methods_and_bad_content_type_use_problem_details()
    {
        using var rig = new TestRig();
        await AssertProblem(await rig.First.GetAsync("/missing"), 404);
        await AssertProblem(await rig.First.DeleteAsync("/orders"), 405);
        await AssertProblem(await rig.First.PostAsync("/orders", new StringContent("hello")), 415);
    }

    private static async Task AssertProblem(HttpResponseMessage response, int status)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = document.RootElement;
        Assert.Equal(status, body.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("code").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("correlationId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
    }
}
