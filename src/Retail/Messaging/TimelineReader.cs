using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Retail.Data;

namespace Retail.Messaging;

internal sealed class TimelineReader(ConnectionFactory broker, IServiceScopeFactory scopes,
    IHostEnvironment environment, ILogger<TimelineReader> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var connection = await broker.CreateConnectionAsync("timeline-reader", ct);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
                await TimelineHost.DeclareQueuesAsync(channel, ct);
                await channel.BasicQosAsync(0, 16, global: false, ct);
                var ended = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                channel.ChannelShutdownAsync += (_, _) => { ended.TrySetResult(null); return Task.CompletedTask; };
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.UnregisteredAsync += (_, _) => { ended.TrySetResult(null); return Task.CompletedTask; };
                consumer.ReceivedAsync += async (_, delivery) =>
                {
                    if (ended.Task.IsCompleted) return;
                    try { await ConsumeAsync(channel, delivery, ct); }
                    catch (Exception failure) { ended.TrySetResult(failure); }
                };
                await channel.BasicConsumeAsync(TimelineHost.Queue, autoAck: false, consumer, ct);
                var error = await ended.Task.WaitAsync(ct);
                if (error is not null) logger.LogWarning(error, "Timeline reader will retry unacknowledged deliveries.");
                // Disposing the channel returns any unacknowledged deliveries to RabbitMQ.
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning(error, "Timeline reader will reconnect.");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private async Task ConsumeAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        TimelineEvent? message;
        try { message = JsonSerializer.Deserialize<TimelineEvent>(delivery.Body.Span, TimelineHost.Json); }
        catch (JsonException) { message = null; }
        if (message is null || !message.IsValid() || !await RecordAsync(message, delivery.Redelivered, ct))
        {
            logger.LogWarning("Dead-lettering an invalid order timeline event {EventId}", message?.EventId);
            await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, ct);
            return;
        }
        await TimelineHost.CheckpointAsync(environment, "after-projection", ct);
        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
    }

    private async Task<bool> RecordAsync(TimelineEvent message, bool redelivered, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RetailDb>();
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO order_timeline (event_id, order_id, version, kind, actor_id, recorded_at, delivered_at)
            VALUES ({message.EventId}, {message.OrderId}, {message.Version}, {message.Kind},
                {message.ActorId}, {message.RecordedAt.ToUniversalTime()}, now())
            ON CONFLICT DO NOTHING
            """, ct);
        if (inserted == 1) return true;

        var existing = await db.Timeline.AsNoTracking().SingleOrDefaultAsync(row => row.EventId == message.EventId, ct);
        if (existing?.Envelope != message) return false;
        logger.LogInformation("Ignored duplicate timeline event {EventId} (broker redelivery {Redelivered})",
            message.EventId, redelivered);
        return true;
    }
}

