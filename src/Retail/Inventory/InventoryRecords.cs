namespace Retail.Inventory;

public sealed class StockPosition
{
    public required string Sku { get; init; }
    public long OnHand { get; set; }
    public long Reserved { get; set; }
}

public sealed class StockReceipt
{
    public Guid Id { get; init; }
    public required string Sku { get; init; }
    public int Quantity { get; init; }
    public required string Reason { get; init; }
    public required string RequestKey { get; init; }
    public required string StaffId { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
}

public sealed class StockMovement
{
    public Guid Id { get; init; }
    public Guid OperationId { get; init; }
    public required string Kind { get; init; }
    public required string Sku { get; init; }
    public int Quantity { get; init; }
    public required string Reason { get; init; }
    public string? ActorId { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
}

public sealed class StockHold
{
    public Guid OrderId { get; init; }
    public required string Sku { get; init; }
    public int Quantity { get; init; }
    public string State { get; set; } = "active";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; set; }
}
