using System.ComponentModel.DataAnnotations;

namespace OrderManagement.Api.Contracts;

public enum OrderStatus { Pending, Confirmed, Shipped, Delivered, Cancelled }

public sealed class CreateOrderRequest
{
    public Guid CustomerId { get; init; }

    [Required, StringLength(500)]
    public string ShippingAddress { get; init; } = "";

    [Required, MinLength(1), MaxLength(100)]
    public List<CreateOrderItemRequest?> Items { get; init; } = [];
}

public sealed class CreateOrderItemRequest
{
    [Range(1, int.MaxValue)]
    public int ProductId { get; init; }

    [Range(1, 10000)]
    public int Quantity { get; init; }
}

public sealed class UpdateStatusRequest
{
    [Required, EnumDataType(typeof(OrderStatus))]
    public OrderStatus? Status { get; init; }

    [Required, Range(1, long.MaxValue)]
    public long? ExpectedVersion { get; init; }
}

public sealed class CancelOrderRequest
{
    [Required, Range(1, long.MaxValue)]
    public long? ExpectedVersion { get; init; }
}

public sealed class ListOrdersQuery
{
    [EnumDataType(typeof(OrderStatus))]
    public OrderStatus? Status { get; init; }

    public Guid? CustomerId { get; init; }
    public DateTimeOffset? DateFrom { get; init; }
    public DateTimeOffset? DateTo { get; init; }

    [Range(1, int.MaxValue)]
    public int Page { get; init; } = 1;

    [Range(1, 100)]
    public int PageSize { get; init; } = 20;
}

public sealed record OrderItemResponse(int ProductId, string ProductName, int Quantity, long UnitPrice)
{
    public long Subtotal => Quantity * UnitPrice;
}

public sealed record OrderResponse(
    Guid Id, Guid CustomerId, string ShippingAddress, OrderStatus Status,
    long Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<OrderItemResponse> Items)
{
    public long Total => Items.Sum(item => item.Subtotal);
}

public sealed record OrderSummary(
    Guid Id, Guid CustomerId, OrderStatus Status, long Version, DateTimeOffset CreatedAt, long Total);

public sealed record PagedOrders(
    IReadOnlyList<OrderSummary> Items, int Page, int PageSize, long TotalCount)
{
    public long TotalPages => (TotalCount + PageSize - 1) / PageSize;
}

public sealed record ProductResponse(int Id, string Name, int StockQuantity, long Price);
