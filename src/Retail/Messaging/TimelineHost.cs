using System.Text.Json;
using RabbitMQ.Client;
using Retail.Data;

namespace Retail.Messaging;

internal static class TimelineHost
{
    public const string Queue = "orders.timeline";
    public const string DeadLetterQueue = "orders.timeline.dead";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Task RunAsync(string role)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddRetailDb();
        builder.Services.AddSingleton(new ConnectionFactory
        {
            HostName = builder.Configuration["Messaging:Host"] ?? "rabbitmq",
            UserName = "timeline",
            Password = builder.Configuration["Messaging:Password"]
                ?? throw new InvalidOperationException("Set Messaging:Password for the timeline workers."),
            AutomaticRecoveryEnabled = false
        });
        if (role == "relay") builder.Services.AddHostedService<TimelineRelay>();
        else builder.Services.AddHostedService<TimelineReader>();
        return builder.Build().RunAsync();
    }

    public static async Task DeclareQueuesAsync(IChannel channel, CancellationToken ct)
    {
        await channel.QueueDeclareAsync(DeadLetterQueue, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: ct);
        await channel.QueueDeclareAsync(Queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = "",
                ["x-dead-letter-routing-key"] = DeadLetterQueue
            }, cancellationToken: ct);
    }

    public static async Task CheckpointAsync(IHostEnvironment environment, string name, CancellationToken ct)
    {
        if (!environment.IsEnvironment("TimelineTest")) return;
        var marker = Path.Combine(Path.GetTempPath(), name);
        if (!File.Exists(marker)) return;
        File.Move(marker, marker + ".paused");
        while (File.Exists(marker + ".paused")) await Task.Delay(100, ct);
    }
}

