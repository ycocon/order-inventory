using Retail.Orders;

namespace Retail.Messaging;

internal sealed class OutboxEntry
{
    public Guid EventId { get; init; }
    public required OrderEvent Event { get; init; }
    public DateTimeOffset? PublishedAt { get; set; }
}

internal sealed record TimelineEvent(Guid EventId, Guid OrderId, int Version, string Kind,
    DateTimeOffset RecordedAt, string? ActorId)
{
    public static TimelineEvent From(OrderEvent orderEvent) =>
        new(orderEvent.Id, orderEvent.OrderId, orderEvent.Version, orderEvent.Kind, orderEvent.RecordedAt, orderEvent.ActorId);

    public bool IsValid() =>
        EventId != Guid.Empty && OrderId != Guid.Empty && RecordedAt != default && ActorId is not { Length: > 450 }
        && (Version, Kind) is (1, "OrderAwaitingPayment" or "OrderRejected")
            or (2, "OrderConfirmed" or "OrderRejected") or (3, "OrderDispatched");
}

internal sealed class TimelineEntry
{
    public Guid EventId { get; init; }
    public Guid OrderId { get; init; }
    public int Version { get; init; }
    public required string Kind { get; init; }
    public string? ActorId { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
    public DateTimeOffset DeliveredAt { get; init; }

    public TimelineEvent Envelope => new(EventId, OrderId, Version, Kind, RecordedAt, ActorId);
}

