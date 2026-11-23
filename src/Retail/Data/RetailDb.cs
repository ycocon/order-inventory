using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Retail.Catalog;
using Retail.Inventory;
using Retail.Messaging;
using Retail.Orders;
using Retail.Payments;

namespace Retail.Data;

public sealed class RetailDb(DbContextOptions<RetailDb> options) : IdentityDbContext<IdentityUser>(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<StockPosition> StockPositions => Set<StockPosition>();
    public DbSet<StockReceipt> Receipts => Set<StockReceipt>();
    public DbSet<StockMovement> Movements => Set<StockMovement>();
    public DbSet<StockHold> Holds => Set<StockHold>();
    public DbSet<CustomerOrder> Orders => Set<CustomerOrder>();
    public DbSet<OrderEvent> OrderEvents => Set<OrderEvent>();
    public DbSet<OrderDispatch> Dispatches => Set<OrderDispatch>();
    public DbSet<PaymentIntent> PaymentIntents => Set<PaymentIntent>();
    public DbSet<PaymentWork> PaymentWorkItems => Set<PaymentWork>();
    internal DbSet<OutboxEntry> Outbox => Set<OutboxEntry>();
    internal DbSet<TimelineEntry> Timeline => Set<TimelineEntry>();

    internal void RecordOrderEvent(OrderEvent orderEvent)
    {
        OrderEvents.Add(orderEvent);
        Outbox.Add(new OutboxEntry { Event = orderEvent, EventId = orderEvent.Id });
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);

        var products = model.Entity<Product>();
        products.ToTable("catalog_products", t => t.HasCheckConstraint("price_nonnegative", "unit_price_minor >= 0"));
        products.HasKey(p => p.Sku);
        products.Property(p => p.Sku).HasMaxLength(48);
        products.Property(p => p.Name).HasMaxLength(160);

        var positions = model.Entity<StockPosition>();
        positions.ToTable("stock_positions", t => t.HasCheckConstraint("valid_stock_balance", "reserved >= 0 AND on_hand >= reserved"));
        positions.HasKey(p => p.Sku);
        positions.HasOne<Product>().WithOne().HasForeignKey<StockPosition>(p => p.Sku).OnDelete(DeleteBehavior.Restrict);

        var receipts = model.Entity<StockReceipt>();
        receipts.ToTable("stock_receipts", t => t.HasCheckConstraint("receipt_quantity_positive", "quantity > 0"));
        receipts.HasKey(r => r.Id);
        receipts.HasIndex(r => new { r.StaffId, r.RequestKey }).IsUnique();
        receipts.Property(r => r.RequestKey).HasMaxLength(128);
        receipts.Property(r => r.Reason).HasMaxLength(300);
        receipts.HasOne<StockPosition>().WithMany().HasForeignKey(r => r.Sku).OnDelete(DeleteBehavior.Restrict);
        receipts.HasOne<IdentityUser>().WithMany().HasForeignKey(r => r.StaffId).OnDelete(DeleteBehavior.Restrict);

        var movements = model.Entity<StockMovement>();
        movements.ToTable("stock_movements", t => t.HasCheckConstraint("movement_quantity_positive", "quantity > 0"));
        movements.HasKey(m => m.Id);
        movements.HasIndex(m => new { m.Kind, m.OperationId, m.Sku }).IsUnique();
        movements.HasIndex(m => new { m.Sku, m.RecordedAt });
        movements.HasOne<StockPosition>().WithMany().HasForeignKey(m => m.Sku).OnDelete(DeleteBehavior.Restrict);
        movements.HasOne<IdentityUser>().WithMany().HasForeignKey(m => m.ActorId).OnDelete(DeleteBehavior.Restrict);
        movements.Property(m => m.Reason).HasMaxLength(300);

        var orders = model.Entity<CustomerOrder>();
        orders.ToTable("customer_orders", t => t.HasCheckConstraint("order_total_nonnegative", "total_minor >= 0"));
        orders.HasKey(o => o.Id);
        orders.HasIndex(o => new { o.CustomerId, o.CheckoutKey }).IsUnique();
        orders.HasIndex(o => new { o.CustomerId, o.CreatedAt });
        orders.Property(o => o.CheckoutKey).HasMaxLength(128);
        orders.Property(o => o.Currency).HasMaxLength(3);
        orders.Property(o => o.Status).HasConversion<string>().HasMaxLength(32);
        orders.HasOne<IdentityUser>().WithMany().HasForeignKey(o => o.CustomerId).OnDelete(DeleteBehavior.Restrict);
        orders.HasMany(o => o.Lines).WithOne().HasForeignKey(l => l.OrderId).OnDelete(DeleteBehavior.Restrict);

        var lines = model.Entity<OrderLine>();
        lines.ToTable("order_lines", t => t.HasCheckConstraint("valid_order_line", "quantity > 0 AND unit_price_minor >= 0"));
        lines.HasKey(l => new { l.OrderId, l.Sku });
        lines.Property(l => l.ProductName).HasMaxLength(160);
        lines.HasOne<Product>().WithMany().HasForeignKey(l => l.Sku).OnDelete(DeleteBehavior.Restrict);

        var holds = model.Entity<StockHold>();
        holds.ToTable("stock_holds", t => t.HasCheckConstraint("hold_quantity_positive", "quantity > 0"));
        holds.HasKey(h => new { h.OrderId, h.Sku });
        holds.HasOne<OrderLine>().WithOne().HasForeignKey<StockHold>(h => new { h.OrderId, h.Sku }).OnDelete(DeleteBehavior.Restrict);
        holds.HasOne<StockPosition>().WithMany().HasForeignKey(h => h.Sku).OnDelete(DeleteBehavior.Restrict);

        var events = model.Entity<OrderEvent>();
        events.ToTable("order_events");
        events.HasKey(e => e.Id);
        events.HasIndex(e => new { e.OrderId, e.Version }).IsUnique();
        events.HasOne<CustomerOrder>().WithMany().HasForeignKey(e => e.OrderId).OnDelete(DeleteBehavior.Restrict);
        events.HasOne<IdentityUser>().WithMany().HasForeignKey(e => e.ActorId).OnDelete(DeleteBehavior.Restrict);

        var outbox = model.Entity<OutboxEntry>();
        outbox.ToTable("order_outbox");
        outbox.HasKey(e => e.EventId);
        outbox.HasIndex(e => e.EventId).HasDatabaseName("ix_order_outbox_unsent").HasFilter("published_at IS NULL");
        outbox.HasOne(e => e.Event).WithOne().HasForeignKey<OutboxEntry>(e => e.EventId).OnDelete(DeleteBehavior.Restrict);

        var timeline = model.Entity<TimelineEntry>();
        timeline.ToTable("order_timeline");
        timeline.HasKey(e => e.EventId);
        timeline.HasIndex(e => new { e.OrderId, e.Version }).IsUnique();
        timeline.Ignore(e => e.Envelope);

        var dispatches = model.Entity<OrderDispatch>();
        dispatches.ToTable("order_dispatches");
        dispatches.HasKey(d => d.OrderId);
        dispatches.HasOne<CustomerOrder>().WithOne().HasForeignKey<OrderDispatch>(d => d.OrderId).OnDelete(DeleteBehavior.Restrict);
        dispatches.HasOne<IdentityUser>().WithMany().HasForeignKey(d => d.StaffId).OnDelete(DeleteBehavior.Restrict);

        var payment = model.Entity<PaymentIntent>();
        payment.ToTable("payment_intents", t => t.HasCheckConstraint("payment_amount_nonnegative", "amount_minor >= 0"));
        payment.HasKey(p => p.OperationId);
        payment.Property(p => p.Currency).HasMaxLength(3);
        payment.HasOne<CustomerOrder>().WithOne().HasForeignKey<PaymentIntent>(p => p.OrderId).OnDelete(DeleteBehavior.Restrict);
        payment.HasOne(p => p.Work).WithOne().HasForeignKey<PaymentWork>(w => w.OperationId).OnDelete(DeleteBehavior.Restrict);

        var work = model.Entity<PaymentWork>();
        work.ToTable("payment_work");
        work.HasKey(w => w.OperationId);
        work.HasIndex(w => new { w.Status, w.DueAt });

        foreach (var entity in new[] { products.Metadata, positions.Metadata, receipts.Metadata, movements.Metadata,
            orders.Metadata, lines.Metadata, holds.Metadata, events.Metadata, outbox.Metadata, timeline.Metadata,
            dispatches.Metadata, payment.Metadata, work.Metadata })
        {
            foreach (var property in entity.GetProperties())
                property.SetColumnName(JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name));
        }
    }
}
