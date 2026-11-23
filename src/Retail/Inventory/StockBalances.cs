using Microsoft.EntityFrameworkCore;
using Retail.Data;
using Retail.Orders;

namespace Retail.Inventory;

internal sealed record Availability
{
    public required string Sku { get; init; }
    public long Units { get; init; }
}

internal static class StockBalances
{
    // The catalog commits a new SKU and its initial position in one SaveChanges.
    public static void Open(RetailDb db, string sku) =>
        db.StockPositions.Add(new StockPosition { Sku = sku });

    public static IQueryable<Availability> Available(RetailDb db) =>
        db.StockPositions.Select(stock => new Availability { Sku = stock.Sku, Units = stock.OnHand - stock.Reserved });

    public static async Task<bool> TryReserveAsync(RetailDb db, Guid orderId, string actorId,
        IReadOnlyCollection<OrderLine> lines, DateTimeOffset now, CancellationToken ct)
    {
        var positions = await LockPositionsAsync(db, lines.Select(line => line.Sku).ToArray(), ct);
        if (lines.Any(line => !positions.TryGetValue(line.Sku, out var position)
                || position.OnHand - position.Reserved < line.Quantity))
            return false;

        foreach (var line in lines)
        {
            positions[line.Sku].Reserved += line.Quantity;
            db.Holds.Add(new StockHold { OrderId = orderId, Sku = line.Sku, Quantity = line.Quantity, CreatedAt = now });
            db.Movements.Add(new StockMovement
            {
                Id = Guid.NewGuid(), OperationId = orderId, Kind = "reserve", ActorId = actorId,
                Sku = line.Sku, Quantity = line.Quantity, Reason = "Checkout reservation", RecordedAt = now
            });
        }
        return true;
    }

    public static Task ReleaseAsync(RetailDb db, CustomerOrder order, DateTimeOffset now, CancellationToken ct) =>
        ResolveHoldsAsync(db, order, null, now, ct);

    public static Task DispatchAsync(RetailDb db, CustomerOrder order, string staffId, DateTimeOffset now, CancellationToken ct) =>
        ResolveHoldsAsync(db, order, staffId, now, ct);

    private static async Task ResolveHoldsAsync(RetailDb db, CustomerOrder order, string? dispatchStaffId,
        DateTimeOffset now, CancellationToken ct)
    {
        var holds = await db.Holds.Where(hold => hold.OrderId == order.Id && hold.State == "active")
            .ToDictionaryAsync(hold => hold.Sku, ct);
        if (holds.Count != order.Lines.Count || order.Lines.Any(line =>
            !holds.TryGetValue(line.Sku, out var hold) || hold.Quantity != line.Quantity))
            throw new InvalidOperationException("The active holds do not match the order.");

        var positions = await LockPositionsAsync(db, holds.Keys.ToArray(), ct);
        var dispatching = dispatchStaffId is not null;
        foreach (var hold in holds.Values)
        {
            var position = positions[hold.Sku];
            position.Reserved -= hold.Quantity;
            if (dispatching) position.OnHand -= hold.Quantity;
            hold.State = dispatching ? "consumed" : "released";
            hold.ResolvedAt = now;
            db.Movements.Add(new StockMovement
            {
                Id = Guid.NewGuid(), OperationId = order.Id, Kind = dispatching ? "dispatch" : "release",
                ActorId = dispatchStaffId, Sku = hold.Sku, Quantity = hold.Quantity,
                Reason = dispatching ? "Order dispatched" : "Payment declined", RecordedAt = now
            });
        }
    }

    // A no-key update lock permits foreign-key checks from receipts while serializing stock changes.
    private static Task<Dictionary<string, StockPosition>> LockPositionsAsync(RetailDb db, string[] skus, CancellationToken ct) =>
        db.StockPositions.FromSqlInterpolated($"""
            SELECT sku, on_hand, reserved FROM stock_positions
            WHERE sku = ANY ({skus})
            ORDER BY sku COLLATE "C"
            FOR NO KEY UPDATE
            """).ToDictionaryAsync(position => position.Sku, ct);
}
