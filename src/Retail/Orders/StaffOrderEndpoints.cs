using System.Data;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Retail.Data;
using Retail.Inventory;

namespace Retail.Orders;

internal sealed record StaffOrderSummary(Guid OrderId, string Customer, string Status, string? RejectionReason,
    long TotalMinor, string Currency, DateTimeOffset CreatedAt);
internal sealed record DispatchView(Guid OrderId, string StaffId, DateTimeOffset DispatchedAt);
internal sealed record HistoryView(string Kind, DateTimeOffset RecordedAt, string? Actor);

internal static class StaffOrderEndpoints
{
    private const int PageSize = 6;

    public static void MapStaffOrders(this RouteGroupBuilder api, bool timelineEnabled)
    {
        var orders = api.MapGroup("/staff/orders").RequireAuthorization(policy => policy.RequireRole("staff"));
        orders.MapGet("", ListAsync);
        orders.MapPost("/{orderId:guid}/resume-payment", ResumeAsync);
        orders.MapGet("/{orderId:guid}", (Guid orderId, RetailDb db, CancellationToken ct) =>
            DetailAsync(orderId, db, timelineEnabled, ct));
        orders.MapPost("/{orderId:guid}/dispatch", DispatchAsync);
    }

    private static async Task<IResult> ListAsync(string? q, string? status, int? page, RetailDb db, CancellationToken ct)
    {
        var term = q?.Trim().ToLowerInvariant() ?? "";
        if (term.Length > 256) return Results.BadRequest(new { error = "Search text is too long." });
        var requestedStatus = status switch
        {
            "awaiting_payment" => OrderStatus.AwaitingPayment,
            "confirmed" => OrderStatus.Confirmed,
            "rejected" => OrderStatus.Rejected,
            "dispatched" => OrderStatus.Dispatched,
            _ => (OrderStatus?)null
        };
        if (!string.IsNullOrEmpty(status) && requestedStatus is null && status != "overdue")
            return Results.BadRequest(new { error = "Choose a valid order status." });

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var orders = from order in db.Orders.AsNoTracking()
                     join customer in db.Users.AsNoTracking() on order.CustomerId equals customer.Id
                     where (!requestedStatus.HasValue || order.Status == requestedStatus)
                        && (status != "overdue" || db.PaymentIntents.Any(p => p.OrderId == order.Id && p.Work.Status == "overdue"))
                        && (term == "" || order.Id.ToString().Contains(term)
                            || (customer.Email ?? "").ToLower().Contains(term))
                     select new { Order = order, Customer = customer.Email ?? customer.Id };

        var total = await orders.CountAsync(ct);
        var currentPage = Math.Clamp(page ?? 1, 1, Math.Max(1, (total - 1) / PageSize + 1));
        var rows = await orders.OrderByDescending(row => row.Order.CreatedAt).ThenByDescending(row => row.Order.Id)
            .Skip((currentPage - 1) * PageSize).Take(PageSize).ToListAsync(ct);
        return Results.Ok(new
        {
            page = currentPage, pageSize = PageSize, total,
            items = rows.Select(row => new StaffOrderSummary(row.Order.Id, row.Customer, OrderView.StatusName(row.Order.Status),
                row.Order.RejectionReason, row.Order.TotalMinor, row.Order.Currency, row.Order.CreatedAt))
        });
    }

    private static async Task<IResult> DetailAsync(Guid orderId, RetailDb db, bool timelineEnabled, CancellationToken ct)
    {
        // Details span several tables. Read a single committed snapshot even during dispatch.
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var order = await db.Orders.AsNoTracking().Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null) return Results.NotFound();

        var customer = await db.Users.Where(u => u.Id == order.CustomerId).Select(u => u.Email).SingleAsync(ct);
        var payment = await db.PaymentIntents.Where(p => p.OrderId == orderId).Select(p => new
        {
            p.OperationId, p.AmountMinor, p.Currency, p.Decision,
            workStatus = p.Work.Status, p.Work.AttemptCount, p.Work.LastError
        }).SingleOrDefaultAsync(ct);
        var dispatch = await db.Dispatches.Where(d => d.OrderId == orderId).Select(d => new
        {
            d.OrderId, d.StaffId, d.DispatchedAt,
            staff = db.Users.Where(u => u.Id == d.StaffId).Select(u => u.Email).SingleOrDefault()
        }).SingleOrDefaultAsync(ct);
        var source = db.OrderEvents.Where(e => e.OrderId == orderId);
        var projection = db.Timeline.Where(e => e.OrderId == orderId);
        var history = timelineEnabled
            ? await projection.OrderBy(e => e.Version).Select(e => new HistoryView(e.Kind, e.RecordedAt,
                db.Users.Where(u => u.Id == e.ActorId).Select(u => u.Email).SingleOrDefault())).ToListAsync(ct)
            : await source.OrderBy(e => e.Version).Select(e => new HistoryView(e.Kind, e.RecordedAt,
                db.Users.Where(u => u.Id == e.ActorId).Select(u => u.Email).SingleOrDefault())).ToListAsync(ct);
        var timelinePending = timelineEnabled && await source
            .AnyAsync(e => !projection.Any(p => p.EventId == e.Id), ct);
        var timelineUpdatedAt = timelineEnabled ? await projection.MaxAsync(e => (DateTimeOffset?)e.DeliveredAt, ct) : null;
        var stock = await db.Movements.Where(m => m.OperationId == orderId)
            .OrderBy(m => m.RecordedAt).ThenBy(m => m.Sku).Select(m => new
            {
                m.Id, m.Kind, m.Sku, m.Quantity, m.Reason, m.RecordedAt,
                actor = db.Users.Where(u => u.Id == m.ActorId).Select(u => u.Email).SingleOrDefault()
            }).ToListAsync(ct);
        return Results.Ok(new { order = OrderView.From(order), customer, payment, dispatch, history, stock,
            timelinePending, timelineUpdatedAt });
    }

    private static async Task<IResult> ResumeAsync(Guid orderId, RetailDb db, CancellationToken ct)
    {
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE payment_work AS w SET status = 'due', attempt_count = 0, due_at = now()
            FROM payment_intents AS i
            WHERE i.operation_id = w.operation_id AND i.order_id = {orderId}
              AND i.decision = 'unresolved' AND w.status = 'overdue'
            """, ct);
        return changed == 1 ? Results.NoContent() : Results.Conflict(new { error = "Only overdue payments can resume." });
    }

    private static async Task<IResult> DispatchAsync(Guid orderId, ClaimsPrincipal user, RetailDb db, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Serialize all dispatch requests for this reference before reading its status or consuming stock.
        var rows = await db.Orders.FromSqlInterpolated($"""
            SELECT * FROM customer_orders WHERE id = {orderId} FOR NO KEY UPDATE
            """).ToListAsync(ct);
        if (rows.SingleOrDefault() is not { } order) return Results.NotFound();
        if (order.Status == OrderStatus.Dispatched)
        {
            var original = await db.Dispatches.AsNoTracking().SingleAsync(d => d.OrderId == orderId, ct);
            return Results.Ok(ToView(original));
        }
        if (order.Status != OrderStatus.Confirmed)
            return Results.Conflict(new { error = "Only confirmed orders can be dispatched." });

        var staffId = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var utc = DateTimeOffset.UtcNow;
        var now = new DateTimeOffset(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);
        await db.Entry(order).Collection(o => o.Lines).LoadAsync(ct);
        await StockBalances.DispatchAsync(db, order, staffId, now, ct);
        var dispatch = new OrderDispatch { OrderId = orderId, StaffId = staffId, DispatchedAt = now };
        db.Dispatches.Add(dispatch);
        order.Status = OrderStatus.Dispatched;
        db.RecordOrderEvent(new OrderEvent
        {
            Id = Guid.NewGuid(), OrderId = orderId, Version = 3, Kind = "OrderDispatched",
            ActorId = staffId, RecordedAt = now
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Created($"/api/staff/orders/{orderId}", ToView(dispatch));
    }

    private static DispatchView ToView(OrderDispatch dispatch) =>
        new(dispatch.OrderId, dispatch.StaffId, dispatch.DispatchedAt);
}
