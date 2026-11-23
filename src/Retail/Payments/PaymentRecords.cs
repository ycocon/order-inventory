namespace Retail.Payments;

public sealed class PaymentIntent
{
    public Guid OperationId { get; init; }
    public Guid OrderId { get; init; }
    public long AmountMinor { get; init; }
    public required string Currency { get; init; }
    public string Decision { get; set; } = "unresolved";
    public required PaymentWork Work { get; init; }
}

public sealed class PaymentWork
{
    public Guid OperationId { get; init; }
    // During processing, DueAt is the lease expiry.
    public DateTimeOffset DueAt { get; set; }
    public int AttemptCount { get; set; }
    public string Status { get; set; } = "due";
    public Guid? LeaseToken { get; set; }
    public string? LastError { get; set; }
}
