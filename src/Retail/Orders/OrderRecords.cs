namespace Retail.Orders;

public enum OrderStatus { AwaitingPayment, Rejected, Confirmed, Dispatched }

public sealed class CustomerOrder
{
    public Guid Id { get; init; }
    public required string CustomerId { get; init; }
    public required string CheckoutKey { get; init; }
    public OrderStatus Status { get; set; }
    public string? RejectionReason { get; set; }
    public long TotalMinor { get; init; }
    public required string Currency { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public List<OrderLine> Lines { get; init; } = [];
}

public sealed class OrderLine
{
    public Guid OrderId { get; init; }
    public required string Sku { get; init; }
    public required string ProductName { get; init; }
    public int Quantity { get; init; }
    public long UnitPriceMinor { get; init; }
}

public sealed class OrderEvent
{
    public Guid Id { get; init; }
    public Guid OrderId { get; init; }
    public int Version { get; init; }
    public required string Kind { get; init; }
    public string? ActorId { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
}

public sealed class OrderDispatch
{
    public Guid OrderId { get; init; }
    public required string StaffId { get; init; }
    public DateTimeOffset DispatchedAt { get; init; }
}

public sealed record OrderView(Guid OrderId, string Status, string? RejectionReason, long TotalMinor,
    string Currency, DateTimeOffset CreatedAt, IReadOnlyList<OrderLineView> Lines)
{
    internal static OrderView From(CustomerOrder order) =>
        new(order.Id, StatusName(order.Status), order.RejectionReason, order.TotalMinor, order.Currency, order.CreatedAt,
            order.Lines.OrderBy(line => line.Sku, StringComparer.Ordinal)
                .Select(line => new OrderLineView(line.Sku, line.ProductName, line.Quantity, line.UnitPriceMinor)).ToArray());

    internal static string StatusName(OrderStatus status) => status switch
    {
        OrderStatus.AwaitingPayment => "awaiting_payment",
        OrderStatus.Confirmed => "confirmed",
        OrderStatus.Rejected => "rejected",
        OrderStatus.Dispatched => "dispatched",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };
}

public sealed record OrderLineView(string Sku, string ProductName, int Quantity, long UnitPriceMinor);
