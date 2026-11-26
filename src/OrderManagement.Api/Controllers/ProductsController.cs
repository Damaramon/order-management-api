using Microsoft.AspNetCore.Mvc;
using OrderManagement.Api.Contracts;
using OrderManagement.Api.Orders;

namespace OrderManagement.Api.Controllers;

[ApiController]
[Route("products")]
public sealed class ProductsController(OrderStore orders) : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<ProductResponse>> List() => Ok(orders.Products());
}
