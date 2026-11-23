using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using Retail.Data;

namespace Retail.Messaging;

internal sealed class TimelineRelay(ConnectionFactory broker, IServiceScopeFactory scopes,
    IHostEnvironment environment, ILogger<TimelineRelay> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var connection = await broker.CreateConnectionAsync("timeline-relay", ct);
                await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(
                    publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
                await TimelineHost.DeclareQueuesAsync(channel, ct);
                while (!ct.IsCancellationRequested && channel.IsOpen)
                {
                    if (!await PublishBatchAsync(channel, ct)) await Task.Delay(250, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning(error, "Timeline publisher will retry unsent events.");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private async Task<bool> PublishBatchAsync(IChannel channel, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RetailDb>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var entries = await db.Outbox.FromSqlRaw("""
            SELECT * FROM order_outbox WHERE published_at IS NULL
            ORDER BY event_id LIMIT 32 FOR UPDATE SKIP LOCKED
            """).Include(entry => entry.Event).ToListAsync(ct);
        if (entries.Count == 0) return false;

        foreach (var entry in entries)
        {
            var message = TimelineEvent.From(entry.Event);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await channel.BasicPublishAsync("", TimelineHost.Queue, mandatory: true,
                new BasicProperties { Persistent = true, ContentType = "application/json", MessageId = message.EventId.ToString() },
                JsonSerializer.SerializeToUtf8Bytes(message, TimelineHost.Json), deadline.Token);
            await TimelineHost.CheckpointAsync(environment, "after-publish", ct);
            entry.PublishedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }
}

