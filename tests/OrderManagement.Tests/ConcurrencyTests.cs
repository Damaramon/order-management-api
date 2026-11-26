using System.Net;
using System.Net.Http.Json;
using OrderManagement.Api.Contracts;
using Xunit;

namespace OrderManagement.Tests;

public sealed class ConcurrencyTests
{
    [Fact]
    public async Task A_two_orders_needing_ten_units_cannot_oversell_fifteen()
    {
        using var rig = new TestRig();
        var responses = await TestRig.Race(
            () => TestRig.Create(rig.First, TestRig.Payload(10), "stock-a"),
            () => TestRig.Create(rig.Second, TestRig.Payload(10), "stock-b"));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        var loser = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Contains("insufficient_stock", await loser.Content.ReadAsStringAsync());
        Assert.Equal(5, rig.Stock());
        Assert.Equal(1, rig.Scalar("SELECT COUNT(*) FROM Orders;"));
        Assert.Equal(1, rig.Scalar("SELECT COUNT(*) FROM IdempotencyRequests;"));
    }

    [Fact]
    public async Task B_shipping_and_cancelling_the_same_version_has_only_one_winner()
    {
        using var rig = new TestRig();
        var created = await TestRig.ReadOrder(await TestRig.Create(rig.First, TestRig.Payload(), "status-race"));
        var confirmed = await rig.First.PatchAsJsonAsync($"/orders/{created.Id}/status",
            new { status = "Confirmed", expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);

        var responses = await TestRig.Race(
            () => rig.First.PatchAsJsonAsync($"/orders/{created.Id}/status", new { status = "Shipped", expectedVersion = 2 }),
            () => rig.Second.PostAsJsonAsync($"/orders/{created.Id}/cancel", new { expectedVersion = 2 }));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        var loser = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Contains("version_conflict", await loser.Content.ReadAsStringAsync());
        var final = await TestRig.ReadOrder(await rig.First.GetAsync($"/orders/{created.Id}"));
        Assert.Equal(3, final.Version);
        Assert.Contains(final.Status, new[] { OrderStatus.Shipped, OrderStatus.Cancelled });
        Assert.Equal(final.Status == OrderStatus.Cancelled ? 15 : 13, rig.Stock());
    }

    [Fact]
    public async Task C_twenty_same_key_requests_across_two_hosts_create_one_order()
    {
        using var rig = new TestRig();
        var payload = TestRig.Payload(3);
        var responses = await TestRig.Race(Enumerable.Range(0, 20)
            .Select<int, Func<Task<HttpResponseMessage>>>(i => () => TestRig.Create(
                i % 2 == 0 ? rig.First : rig.Second, payload, "same-key")).ToArray());

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        var bodies = await Task.WhenAll(responses.Select(response => response.Content.ReadAsStringAsync()));
        Assert.Single(bodies.Distinct());
        Assert.Single(responses, response => response.Headers.GetValues("Idempotency-Replayed").Single() == "false");
        Assert.Equal(1, rig.Scalar("SELECT COUNT(*) FROM Orders;"));
        Assert.Equal(1, rig.Scalar("SELECT COUNT(*) FROM IdempotencyRequests;"));
        Assert.Equal(12, rig.Stock());
    }

    [Fact]
    public async Task Two_cancellations_through_different_routes_restore_stock_once()
    {
        using var rig = new TestRig();
        var order = await TestRig.ReadOrder(await TestRig.Create(rig.First, TestRig.Payload(4), "cancel-race"));
        var responses = await TestRig.Race(
            () => rig.First.PostAsJsonAsync($"/orders/{order.Id}/cancel", new { expectedVersion = 1 }),
            () => rig.Second.PatchAsJsonAsync($"/orders/{order.Id}/status", new { status = "Cancelled", expectedVersion = 1 }));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(15, rig.Stock());
        var retry = await rig.First.PostAsJsonAsync($"/orders/{order.Id}/cancel", new { expectedVersion = 2 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, retry.StatusCode);
        Assert.Equal(15, rig.Stock());
    }

    [Fact]
    public async Task Same_key_with_different_payloads_returns_conflict_without_extra_deduction()
    {
        using var rig = new TestRig();
        var responses = await TestRig.Race(
            () => TestRig.Create(rig.First, TestRig.Payload(2), "different-payload"),
            () => TestRig.Create(rig.Second, TestRig.Payload(3), "different-payload"));
        var winner = await TestRig.ReadOrder(Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created));
        var loser = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Contains("idempotency_key_reused", await loser.Content.ReadAsStringAsync());
        Assert.Equal(15 - winner.Items.Single().Quantity, rig.Stock());
        Assert.Equal(1, rig.Scalar("SELECT COUNT(*) FROM Orders;"));
    }
}
