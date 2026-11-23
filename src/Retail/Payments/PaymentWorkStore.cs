using Microsoft.EntityFrameworkCore;
using Retail.Data;
using Retail.Inventory;
using Retail.Orders;

namespace Retail.Payments;

internal sealed record ClaimedPayment(Guid OperationId, Guid LeaseToken, long AmountMinor, string Currency, bool ReconcileFirst);

internal sealed class PaymentWorkStore(RetailDb db)
{
    private const int MaxAttempts = 8;
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    public async Task<ClaimedPayment?> ClaimAsync(CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var matches = await db.PaymentWorkItems.FromSqlInterpolated($"""
            SELECT * FROM payment_work
            WHERE status IN ('due', 'processing') AND due_at <= {now}
            ORDER BY due_at, operation_id
            LIMIT 1
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);
        if (matches.SingleOrDefault() is not { } work) return null;

        var intent = await db.PaymentIntents.AsNoTracking().SingleAsync(i => i.OperationId == work.OperationId, ct);
        var reconcileFirst = work.AttemptCount > 0 || work.LastError is not null;
        // An expired lease resumes the interrupted attempt under a new token.
        if (work.Status == "due") work.AttemptCount++;
        var token = Guid.NewGuid();
        work.LeaseToken = token;
        work.Status = "processing";
        work.DueAt = now + Lease;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(intent.OperationId, token, intent.AmountMinor, intent.Currency, reconcileFirst);
    }

    public async Task<string?> FinishAsync(ClaimedPayment payment, SimulatorResult result, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.PaymentWorkItems.FromSqlInterpolated($"""
            SELECT * FROM payment_work
            WHERE operation_id = {payment.OperationId} AND lease_token = {payment.LeaseToken}
                  AND status = 'processing'
            FOR UPDATE
            """).ToListAsync(ct);
        if (rows.SingleOrDefault() is not { } work) return null;

        var now = DateTimeOffset.UtcNow;
        if (result.Decision is null)
        {
            work.LastError = result.Error;
            work.Status = result.NeedsAttention || work.AttemptCount >= MaxAttempts ? "overdue" : "due";
            // Keep delays under 30 seconds, with a small spread for orders that fail together.
            var delay = Math.Min(30, Math.Pow(2, work.AttemptCount) * (0.9 + Random.Shared.NextDouble() * 0.2));
            work.DueAt = work.Status == "due" ? now.AddSeconds(delay) : now;
        }
        else
        {
            var intent = await db.PaymentIntents.SingleAsync(i => i.OperationId == payment.OperationId, ct);
            var order = await db.Orders.Include(o => o.Lines).SingleAsync(o => o.Id == intent.OrderId, ct);
            if (order.Status != OrderStatus.AwaitingPayment || intent.Decision != "unresolved")
                throw new InvalidOperationException("Only pending orders with unresolved payments can be updated.");

            var approved = result.Decision == "approved";
            if (!approved) await StockBalances.ReleaseAsync(db, order, now, ct);
            order.Status = approved ? OrderStatus.Confirmed : OrderStatus.Rejected;
            order.RejectionReason = approved ? null : "payment_declined";
            intent.Decision = result.Decision;
            work.Status = "complete";
            work.LastError = null;
            db.RecordOrderEvent(new OrderEvent
            {
                Id = Guid.NewGuid(), OrderId = order.Id, Version = 2,
                Kind = approved ? "OrderConfirmed" : "OrderRejected", RecordedAt = now
            });
        }

        work.LeaseToken = null;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return work.Status;
    }
}
