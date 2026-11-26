using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using OrderManagement.Api.Contracts;
using OrderManagement.Api.Orders;

namespace OrderManagement.Api.Controllers;

[ApiController]
[Route("orders")]
public sealed class OrdersController(OrderStore orders) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<OrderResponse>(201)]
    public IActionResult Create(CreateOrderRequest request,
        [FromHeader(Name = "Idempotency-Key"), Required] string key, CancellationToken cancellationToken)
    {
        var result = orders.Create(request, key, cancellationToken);
        Response.Headers.Location = $"/orders/{result.OrderId}";
        Response.Headers["Idempotency-Replayed"] = result.Replayed ? "true" : "false";
        return new ContentResult { StatusCode = 201, ContentType = "application/json", Content = result.Json };
    }

    [HttpGet("{id:guid}")]
    public ActionResult<OrderResponse> Get(Guid id)
    {
        var order = orders.Get(id);
        Response.Headers.ETag = $"\"{order.Version}\"";
        return Ok(order);
    }

    [HttpGet]
    public ActionResult<PagedOrders> List([FromQuery] ListOrdersQuery query) => Ok(orders.List(query));

    [HttpPatch("{id:guid}/status")]
    public ActionResult<OrderResponse> Update(Guid id, UpdateStatusRequest request, CancellationToken cancellationToken)
    {
        var order = orders.ChangeStatus(id, request.Status!.Value, request.ExpectedVersion!.Value, cancellationToken);
        Response.Headers.ETag = $"\"{order.Version}\"";
        return Ok(order);
    }

    [HttpPost("{id:guid}/cancel")]
    public ActionResult<OrderResponse> Cancel(Guid id, CancelOrderRequest request, CancellationToken cancellationToken)
    {
        var order = orders.ChangeStatus(id, OrderStatus.Cancelled, request.ExpectedVersion!.Value, cancellationToken);
        Response.Headers.ETag = $"\"{order.Version}\"";
        return Ok(order);
    }
}
