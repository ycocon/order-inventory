namespace Retail.Payments;

internal sealed class PaymentWorker(IServiceScopeFactory scopes, ILogger<PaymentWorker> logger, IHostEnvironment environment) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromMilliseconds(500);
            try
            {
                var payment = await ClaimAsync(stoppingToken);
                if (payment is not null)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var client = scope.ServiceProvider.GetRequiredService<PaymentSimulatorClient>();
                    var result = await client.ResolveAsync(payment, stoppingToken);
                    await WaitAtTestCheckpointAsync("before-finish", stoppingToken);
                    var store = scope.ServiceProvider.GetRequiredService<PaymentWorkStore>();
                    var status = await store.FinishAsync(payment, result, stoppingToken);
                    await WaitAtTestCheckpointAsync("after-finish", stoppingToken);
                    if (status is null) logger.LogInformation("Ignored stale payment claim {OperationId}", payment.OperationId);
                    if (status == "complete")
                        logger.LogInformation("Payment {OperationId} completed as {Decision}", payment.OperationId, result.Decision);
                    if (status == "overdue")
                        logger.LogWarning("Payment {OperationId} needs attention: {Reason}", payment.OperationId, result.Error);
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogError(error, "Payment processing failed; unfinished claims will be recovered after their leases expire.");
                delay = TimeSpan.FromSeconds(1);
            }
            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task WaitAtTestCheckpointAsync(string name, CancellationToken ct)
    {
        if (!environment.IsEnvironment("RecoveryTest")) return;
        var marker = Path.Combine(Path.GetTempPath(), name);
        if (!File.Exists(marker)) return;
        File.Move(marker, marker + ".paused");
        while (File.Exists(marker + ".paused")) await Task.Delay(100, ct);
    }

    private async Task<ClaimedPayment?> ClaimAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentWorkStore>().ClaimAsync(ct);
    }
}
