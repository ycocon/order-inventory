using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Retail.Catalog;
using Retail.Data;
using Retail.Inventory;
using Retail.Payments;

namespace Retail.Orders;

public sealed record CheckoutInput(string? RequestKey, CheckoutLine?[]? Lines);
public sealed record CheckoutLine(string? Sku, int Quantity);

public static class CheckoutEndpoints
{
    private const int MaxBasketLines = 50;

    public static void MapOrders(this RouteGroupBuilder api)
    {
        var orders = api.MapGroup("/orders").RequireAuthorization(policy => policy.RequireRole("customer"));
        orders.MapPost("", SubmitAsync);
        orders.MapGet("", async (ClaimsPrincipal user, RetailDb db, CancellationToken ct) =>
        {
            var orders = await OwnedOrders(db, CustomerId(user)).OrderByDescending(order => order.CreatedAt)
                .ThenByDescending(order => order.Id).Take(25).ToListAsync(ct);
            return orders.Select(OrderView.From);
        });
        orders.MapGet("/{orderId:guid}", async (Guid orderId, ClaimsPrincipal user, RetailDb db, CancellationToken ct) =>
        {
            var order = await OwnedOrders(db, CustomerId(user)).SingleOrDefaultAsync(o => o.Id == orderId, ct);
            return order is null ? Results.NotFound() : Results.Ok(OrderView.From(order));
        });
    }

    private static async Task<IResult> SubmitAsync(CheckoutInput input, ClaimsPrincipal user, RetailDb db, CancellationToken ct)
    {
        var requestKey = input.RequestKey?.Trim();
        if (string.IsNullOrEmpty(requestKey) || requestKey.Length > 128 || input.Lines is not { Length: > 0 and <= MaxBasketLines })
            return Invalid("Provide a checkout key and between 1 and 50 items.");

        var basket = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in input.Lines)
        {
            var sku = Product.NormalizeSku(line?.Sku);
            if (line is null || line.Quantity <= 0 || !Product.IsValidSku(sku) || !basket.TryAdd(sku, line.Quantity))
                return Invalid("Use each SKU once and enter a positive whole quantity.");
        }

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                return await CommitAsync(db, CustomerId(user), requestKey, basket, ct);
            }
            catch (Exception error) when (IsTransactionAbort(error))
            {
                db.ChangeTracker.Clear();
                if (attempt == 3)
                    return Results.Json(new { error = "Checkout is busy. Retry the same basket." }, statusCode: 503);
            }
        }
        throw new InvalidOperationException("The checkout retry loop did not complete.");
    }

    private static async Task<IResult> CommitAsync(RetailDb db, string customerId, string requestKey,
        SortedDictionary<string, int> basket, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Serialize same-key requests before inspecting catalog or touching stock.
        // A hash collision can cause a wait, but the exact database key defines identity.
        var lockKey = $"checkout:{customerId}:{requestKey}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", ct);
        var existing = await OwnedOrders(db, customerId).SingleOrDefaultAsync(o => o.CheckoutKey == requestKey, ct);
        if (existing is not null)
            return existing.Lines.Count == basket.Count && existing.Lines.All(l => basket.GetValueOrDefault(l.Sku) == l.Quantity)
                ? Results.Ok(OrderView.From(existing))
                : Results.Conflict(new { error = "This checkout key already identifies a different basket." });

        var skus = basket.Keys.ToArray();
        var products = await db.Products.AsNoTracking().Where(p => p.Active && skus.Contains(p.Sku))
            .ToDictionaryAsync(p => p.Sku, ct);
        if (products.Count != basket.Count)
            return Invalid("Every item must be active in the catalog.");

        var orderId = Guid.NewGuid();
        var lines = new List<OrderLine>(basket.Count);
        long total = 0;
        foreach (var (sku, quantity) in basket)
        {
            var product = products[sku];
            if (product.UnitPriceMinor > (long.MaxValue - total) / quantity)
                return Invalid("The basket total exceeds the supported range.");
            total += product.UnitPriceMinor * quantity;
            lines.Add(new OrderLine { OrderId = orderId, Sku = sku, ProductName = product.Name,
                Quantity = quantity, UnitPriceMinor = product.UnitPriceMinor });
        }

        const string currency = "USD";
        var now = DateTimeOffset.UtcNow;
        var reserved = await StockBalances.TryReserveAsync(db, orderId, customerId, lines, now, ct);
        var order = new CustomerOrder
        {
            Id = orderId, CustomerId = customerId, CheckoutKey = requestKey, Lines = lines, CreatedAt = now,
            Currency = currency, TotalMinor = total, Status = reserved ? OrderStatus.AwaitingPayment : OrderStatus.Rejected,
            RejectionReason = reserved ? null : "insufficient_stock"
        };
        db.Orders.Add(order);
        db.RecordOrderEvent(new OrderEvent
        {
            Id = Guid.NewGuid(), OrderId = orderId, Version = 1, ActorId = customerId, RecordedAt = now,
            Kind = reserved ? "OrderAwaitingPayment" : "OrderRejected"
        });
        if (reserved)
        {
            var operationId = Guid.NewGuid();
            db.PaymentIntents.Add(new PaymentIntent
            {
                OperationId = operationId, OrderId = orderId, AmountMinor = total, Currency = currency,
                Work = new PaymentWork { OperationId = operationId, DueAt = now }
            });
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Created($"/api/orders/{orderId}", OrderView.From(order));
    }

    private static IQueryable<CustomerOrder> OwnedOrders(RetailDb db, string id) =>
        db.Orders.AsNoTracking().Where(order => order.CustomerId == id).Include(order => order.Lines);

    private static string CustomerId(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private static IResult Invalid(string error) => Results.BadRequest(new { code = "invalid_checkout", error });

    private static bool IsTransactionAbort(Exception error) =>
        (error is DbUpdateException ? error.InnerException : error) is PostgresException
            { SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure };
}
