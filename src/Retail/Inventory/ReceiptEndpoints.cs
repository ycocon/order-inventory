using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Retail.Catalog;
using Retail.Data;

namespace Retail.Inventory;

public sealed record ReceiptInput(string? Sku, int Quantity, string? Reason, string? RequestKey);

public static class ReceiptEndpoints
{
    public static void MapReceipts(this RouteGroupBuilder api)
    {
        var receipts = api.MapGroup("/receipts").RequireAuthorization(policy => policy.RequireRole("staff"));
        receipts.MapPost("", ReceiveAsync);
        receipts.MapGet("/{id:guid}", async (Guid id, RetailDb db, CancellationToken ct) =>
        {
            var receipt = await db.Receipts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
            return receipt is null ? Results.NotFound() : Results.Ok(receipt);
        });
    }

    private static async Task<IResult> ReceiveAsync(ReceiptInput input, ClaimsPrincipal user, RetailDb db, CancellationToken ct)
    {
        var sku = Product.NormalizeSku(input.Sku);
        var reason = input.Reason?.Trim();
        var key = input.RequestKey?.Trim();
        if (!Product.IsValidSku(sku) || input.Quantity <= 0 || string.IsNullOrEmpty(reason) || reason.Length > 300
            || string.IsNullOrEmpty(key) || key.Length > 128)
            return Results.BadRequest(new { code = "invalid_receipt", error = "Provide an SKU, positive whole quantity, reason (up to 300 characters), and request key (up to 128)." });

        var staffId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var previous = db.Receipts.AsNoTracking().Where(r => r.StaffId == staffId && r.RequestKey == key);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var existing = await previous.SingleOrDefaultAsync(ct);
        if (existing is not null) return Replay(existing);
        if (!await db.StockPositions.AnyAsync(s => s.Sku == sku, ct))
            return Results.NotFound(new { error = "Unknown SKU." });

        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        // The unique index serializes even simultaneous first attempts. A new statement
        // at READ COMMITTED can see the winning receipt after ON CONFLICT waits.
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO stock_receipts (id, staff_id, request_key, sku, quantity, reason, recorded_at)
            VALUES ({id}, {staffId}, {key}, {sku}, {input.Quantity}, {reason}, {now})
            ON CONFLICT (staff_id, request_key) DO NOTHING
            """, ct);
        if (inserted == 0) return Replay(await previous.SingleAsync(ct));

        // The increment obtains the position lock; an independent receipt adds to the
        // latest committed balance. Returning here rolls back the uncommitted receipt.
        var updated = await db.StockPositions.Where(s => s.Sku == sku && s.OnHand <= long.MaxValue - input.Quantity)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.OnHand, s => s.OnHand + input.Quantity), ct);
        if (updated != 1) return Results.Conflict(new { error = "The resulting on-hand balance exceeds the supported range." });
        db.Movements.Add(new StockMovement
        {
            Id = Guid.NewGuid(), OperationId = id, Kind = "receive", ActorId = staffId,
            Sku = sku, Quantity = input.Quantity, Reason = reason, RecordedAt = now
        });
        await db.SaveChangesAsync(ct);
        var saved = await previous.SingleAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Created($"/api/receipts/{id}", saved);

        IResult Replay(StockReceipt receipt) =>
            receipt.Sku == sku && receipt.Quantity == input.Quantity && receipt.Reason == reason
                ? Results.Ok(receipt)
                : Results.Conflict(new { error = "This request key already identifies a different receipt." });
    }
}
