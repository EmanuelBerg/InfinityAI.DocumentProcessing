using System.Text;
using System.Text.Json;
using InfinityAI.Api.Models.Ingestion;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace InfinityAI.Api.Services;

public sealed class DocumentIngestionPublisher(
    IConfiguration configuration,
    IOptions<DocumentIngestionOptions> options,
    ILogger<DocumentIngestionPublisher> logger) : IDocumentIngestionPublisher
{
    public async Task PublishAsync(DocumentIngestionJob job, CancellationToken ct = default)
    {
        var queueName = options.Value.QueueName;
        var server    = configuration["RabbitMQServer"] ?? "rabbitmq";
        var port      = int.TryParse(configuration["RabbitMQPort"], out var p) ? p : 5672;

        var factory = new ConnectionFactory
        {
            HostName                 = server,
            Port                     = port,
            AutomaticRecoveryEnabled = true
        };

        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(job));

        await using var connection = await factory.CreateConnectionAsync(ct);
        await using var channel    = await connection.CreateChannelAsync(cancellationToken: ct);

        await channel.QueueDeclareAsync(
            queue:      queueName,
            durable:    true,
            exclusive:  false,
            autoDelete: false,
            cancellationToken: ct);

        await channel.BasicPublishAsync(
            exchange:        "",
            routingKey:      queueName,
            mandatory:       false,
            basicProperties: new BasicProperties { Persistent = true },
            body:            body,
            cancellationToken: ct);

        logger.LogInformation(
            "[DOC-INGEST-QUEUE] Published DocumentId={DocumentId} FileName={FileName} Attempt={Attempt}",
            job.DocumentId, job.FileName, job.Attempt);
    }
}
