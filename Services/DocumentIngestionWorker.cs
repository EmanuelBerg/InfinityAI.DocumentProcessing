using System.Text;
using System.Text.Json;
using InfinityAI.Api.Helpers;
using InfinityAI.Api.Models.Database;
using InfinityAI.Api.Models.Ingestion;
using InfinityAI.Api.Models.Llm;
using InfinityAI.Api.Models.Qdrant;
using InfinityAI.Api.Models.SignalR;
using InfinityAI.Api.Pipeline;
using InfinityAI.Api.Services.Llm;
using InfinityAI.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace InfinityAI.Api.Services;

/// <summary>
/// Background consumer for the "document-ingestion" RabbitMQ queue.
/// Handles extract → pipeline → chunk → embed → index for each queued document.
/// Registered as IHostedService so it shares all DI registrations with InfinityAI.Api.
/// </summary>
public sealed class DocumentIngestionWorker(
    IServiceScopeFactory       scopeFactory,
    IConfiguration             configuration,
    IOptions<DocumentIngestionOptions> options,
    ILogger<DocumentIngestionWorker>   logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("[DOC-INGEST-WORKER] Async ingestion disabled — worker not starting.");
            return;
        }

        logger.LogInformation("[DOC-INGEST-WORKER] Starting. Queue={Queue}", options.Value.QueueName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[DOC-INGEST-WORKER] Connection lost — reconnecting in 5 s");
                await Task.Delay(5_000, stoppingToken);
            }
        }

        logger.LogInformation("[DOC-INGEST-WORKER] Stopped.");
    }

    // ── RabbitMQ consumer loop ────────────────────────────────────────────────

    private async Task ConsumeAsync(CancellationToken ct)
    {
        var server = configuration["RabbitMQServer"] ?? "rabbitmq";
        var port   = int.TryParse(configuration["RabbitMQPort"], out var p) ? p : 5672;
        var queue  = options.Value.QueueName;

        var factory = new ConnectionFactory
        {
            HostName                 = server,
            Port                     = port,
            AutomaticRecoveryEnabled = true
        };

        await using var connection = await factory.CreateConnectionAsync(ct);
        await using var channel    = await connection.CreateChannelAsync(cancellationToken: ct);

        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, cancellationToken: ct);

        var consumer = new AsyncEventingBasicConsumer(channel);

        consumer.ReceivedAsync += async (_, ea) =>
        {
            var raw = Encoding.UTF8.GetString(ea.Body.Span);
            DocumentIngestionJob? job = null;

            try
            {
                job = JsonSerializer.Deserialize<DocumentIngestionJob>(raw);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[DOC-INGEST-WORKER] Failed to deserialize job — discarding");
                await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, ct);
                return;
            }

            if (job is null)
            {
                await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, ct);
                return;
            }

            try
            {
                await ProcessJobAsync(job, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "[DOC-INGEST-WORKER] Unhandled error for DocumentId={DocumentId}",
                    job.DocumentId);
            }
            finally
            {
                await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, ct);
            }
        };

        await channel.BasicConsumeAsync(queue, autoAck: false, consumer: consumer, cancellationToken: ct);

        await Task.Delay(Timeout.Infinite, ct);
    }

    // ── Core processing ───────────────────────────────────────────────────────

    private async Task ProcessJobAsync(DocumentIngestionJob job, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        var db               = sp.GetRequiredService<ApplicationDbContext>();
        var extractionSvc    = sp.GetRequiredService<IDocumentExtractionService>();
        var embeddingBatch   = sp.GetRequiredService<IEmbeddingBatchService>();
        var modelSelection   = sp.GetRequiredService<IModelSelectionService>();
        var qdrantStore      = sp.GetRequiredService<IQdrantVectorStore>();
        var qdrantOpts       = sp.GetRequiredService<IOptions<QdrantOptions>>();
        var pipeline         = sp.GetRequiredService<PipelineOrchestrator>();
        var signalR          = sp.GetRequiredService<SignalRNotificationClient>();
        var opts             = options.Value;

        // ── Load document ──────────────────────────────────────────────────────

        var document = await db.Documents
            .Include(d => d.File)
            .Include(d => d.StoredFile)
            .Include(d => d.Chunks)
            .FirstOrDefaultAsync(d => d.Id == job.DocumentId, ct);

        if (document is null)
        {
            logger.LogWarning("[DOC-INGEST] DocumentId={DocumentId} not found — skipping", job.DocumentId);
            return;
        }

        // ── Idempotency guards (read-only checks before the atomic claim) ──────────

        if (document.StatusEnum == DocumentStatus.Ready)
        {
            logger.LogInformation("[DOC-INGEST] DocumentId={DocumentId} already Ready — skipping", job.DocumentId);
            return;
        }

        if (document.StatusEnum == DocumentStatus.Blocked)
        {
            logger.LogInformation("[DOC-INGEST] DocumentId={DocumentId} is Blocked — skipping", job.DocumentId);
            return;
        }

        if (document.ProcessingAttemptCount >= opts.MaxAttempts)
        {
            logger.LogError(
                "[DOC-INGEST] DocumentId={DocumentId} exceeded MaxAttempts={Max} — marking Failed",
                job.DocumentId, opts.MaxAttempts);
            await SetFailedAsync(db, document, "Maximum processing attempts exceeded.", ct);
            await NotifyAsync(signalR, document, job.UserId, ct);
            return;
        }

        // ── Atomic distributed claim (race-condition-free) ──────────────────────
        // ExecuteUpdateAsync translates to a single UPDATE … WHERE statement.
        // MySQL row-level locking ensures only one worker wins when multiple replicas
        // receive the same job from RabbitMQ simultaneously.

        var now     = DateTime.UtcNow;
        var lockId  = Guid.NewGuid().ToString();

        var lockExpires = now.AddMinutes(opts.ProcessingLockMinutes);
        var workerName  = Environment.MachineName;

        var claimed = await db.Documents
            .Where(d => d.Id == job.DocumentId
                     && d.Status != "Ready"
                     && d.Status != "Blocked"
                     && (d.ProcessingLockId == null || d.ProcessingLockExpiresAt <= now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status,                 "Processing")
                .SetProperty(d => d.ProcessingAttemptCount, d => d.ProcessingAttemptCount + 1)
                .SetProperty(d => d.ProcessingStartedAt,    now)
                .SetProperty(d => d.ProcessingLockId,       lockId)
                .SetProperty(d => d.ProcessingLockExpiresAt, lockExpires)
                .SetProperty(d => d.LastProcessingWorker,   workerName)
                .SetProperty(d => d.UpdatedUtc,             now),
            ct);

        if (claimed == 0)
        {
            logger.LogInformation(
                "[DOC-INGEST] DocumentId={DocumentId} — skipped: claimed by another worker or status already terminal",
                job.DocumentId);
            return;
        }

        // Reload fresh state so the rest of the pipeline works from the committed values
        db.ChangeTracker.Clear();
        document = await db.Documents
            .Include(d => d.File)
            .Include(d => d.StoredFile)
            .Include(d => d.Chunks)
            .FirstAsync(d => d.Id == job.DocumentId, ct);

        // Resolve physical file — prefer ApplicationFile (has full metadata for extraction service)
        ApplicationFile? appFile = document.File;
        if (appFile is null && document.StoredFileId.HasValue)
        {
            // Fall back: build a synthetic ApplicationFile from StoredFile for extraction
            var sf = document.StoredFile;
            if (sf is null)
            {
                await SetFailedAsync(db, document, "No physical file record found.", ct);
                await NotifyAsync(signalR, document, job.UserId, ct);
                return;
            }
            appFile = new ApplicationFile
            {
                Id               = Guid.Empty,
                StoragePath      = sf.StoragePath,
                FileExtension    = sf.FileExtension,
                ContentType      = sf.ContentType,
                OriginalFileName = job.FileName,
            };
        }

        if (appFile is null || !System.IO.File.Exists(appFile.StoragePath))
        {
            await SetFailedAsync(db, document, "Physical file not found on disk.", ct);
            await NotifyAsync(signalR, document, job.UserId, ct);
            return;
        }

        logger.LogInformation(
            "[DOC-INGEST] DocumentId={DocumentId} FileName={FileName} Stage=Processing Attempt={Attempt} Worker={Worker}",
            job.DocumentId, job.FileName, document.ProcessingAttemptCount, document.LastProcessingWorker);

        var startTime = DateTime.UtcNow;

        try
        {
            // ── Stage 1: Extract ──────────────────────────────────────────────────

            await SetStageAsync(db, document, DocumentStatus.Extracting, "Extracting", ct);
            await NotifyAsync(signalR, document, job.UserId, ct);

            var text = await extractionSvc.ExtractTextAsync(appFile, ct);

            if (string.IsNullOrWhiteSpace(text))
            {
                await SetFailedAsync(db, document, "Document contains no readable text.", ct);
                await NotifyAsync(signalR, document, job.UserId, ct);
                return;
            }

            document.ExtractedText = text;

            // For spreadsheets: mark StructuredDataReady immediately so SDI works during embedding
            if (opts.EnableStructuredReadyBeforeEmbeddings &&
                (document.DocumentType == "Excel" || document.DocumentType == "Csv"))
            {
                document.StructuredDataReady = true;
                document.UpdatedUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);

                logger.LogInformation(
                    "[DOC-INGEST] DocumentId={DocumentId} StructuredDataReady=true (SDI available before embeddings)",
                    job.DocumentId);
            }

            // ── Pipeline validation (PII / security) ─────────────────────────────

            var ingestionResult = await pipeline.ExecuteAsync(new InfinityAI.Pipeline.Contracts.PipelineRequest
            {
                RequestId = Guid.NewGuid().ToString(),
                Stage     = "document-ingestion",
                TenantId  = job.UserId?.ToString() ?? "",
                UserId    = job.UserId?.ToString() ?? "",
                Messages  = [new InfinityAI.Pipeline.Contracts.PipelineMessage { Role = "user", Content = text }],
                Context   = new Dictionary<string, object?>
                {
                    ["documentId"]   = document.Id,
                    ["fileId"]       = document.FileId,
                    ["fileName"]     = job.FileName,
                    ["documentType"] = document.DocumentType
                }
            }, ct);

            if (!ingestionResult.ShouldContinue)
            {
                var reason = ingestionResult.ErrorMessage ??
                    "Document blocked by security policy.";

                logger.LogError(
                    "[DOCUMENT-BLOCKED] DocumentId={DocumentId} FileName={FileName} Reason={Reason} Component={Component}",
                    job.DocumentId, job.FileName, reason,
                    ingestionResult.StoppedBy?.Component ?? "unknown");

                document.StatusEnum              = DocumentStatus.Blocked;
                document.BlockReason             = reason;
                document.ErrorMessage            = reason;
                document.ExtractedText           = null;
                document.ProcessingCompletedAt   = DateTime.UtcNow;
                document.ProcessingLockId        = null;
                document.ProcessingLockExpiresAt = null;
                document.UpdatedUtc              = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                await NotifyAsync(signalR, document, job.UserId, ct);
                return;
            }

            // ── Stage 2: Chunk ────────────────────────────────────────────────────

            await SetStageAsync(db, document, DocumentStatus.Chunking, "Chunking", ct);
            await NotifyAsync(signalR, document, job.UserId, ct);

            db.DocumentChunks.RemoveRange(document.Chunks);
            var chunks = EndpointHelpers.CreateDocumentChunks(document.Id, text);
            db.DocumentChunks.AddRange(chunks);
            await db.SaveChangesAsync(ct);

            // ── Stage 3: Embed ────────────────────────────────────────────────────

            var embeddingSelection = await modelSelection.SelectBestModelAsync(
                LlmCapability.Embeddings, profileId: null, ct);

            if (string.IsNullOrWhiteSpace(embeddingSelection.Model.ModelId))
            {
                await SetFailedAsync(db, document, "No enabled embedding model configured.", ct);
                await NotifyAsync(signalR, document, job.UserId, ct);
                return;
            }

            var modelId      = embeddingSelection.Model.ModelId;
            var providerType = embeddingSelection.Model.LlmProvider?.ProviderType ?? "";

            document.StatusEnum      = DocumentStatus.Embedding;
            document.ProcessingStage = "Embedding";
            document.ProgressCurrent = 0;
            document.ProgressTotal   = chunks.Count;
            document.ProgressPercent = 0;
            document.UpdatedUtc      = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await NotifyAsync(signalR, document, job.UserId, ct);

            var texts = chunks.Select(c => c.Content).ToList();
            var progress = new Progress<int>(done =>
            {
                document.ProgressCurrent = done;
                document.ProgressPercent = chunks.Count > 0
                    ? Math.Round((double)done / chunks.Count * 100, 1)
                    : 0;
            });

            var embeddings = await embeddingBatch.CreateEmbeddingsAsync(
                texts, providerType, modelId, progress, ct);

            logger.LogInformation(
                "[DOC-INGEST] DocumentId={DocumentId} Stage=Embedding Current={Current} Total={Total} Percent={Pct:F1}%",
                job.DocumentId, embeddings.Count, chunks.Count,
                chunks.Count > 0 ? embeddings.Count * 100.0 / chunks.Count : 0);

            // ── Stage 4: Index ────────────────────────────────────────────────────

            await SetStageAsync(db, document, DocumentStatus.Indexing, "Indexing", ct);
            await NotifyAsync(signalR, document, job.UserId, ct);

            var qdrantOpts2 = qdrantOpts.Value;
            var qdrantCollectionName = (qdrantOpts2.IsEnabled && qdrantOpts2.DualWriteEnabled)
                ? qdrantStore.GetCollectionName(providerType, modelId)
                : null;

            // Idempotency: remove any stale Qdrant vectors from a previous (failed) run before
            // uploading the new ones. No-op on first run. Protects against duplicate points
            // if a worker crashes between Qdrant upsert and DB SaveChanges.
            if (qdrantCollectionName is not null)
            {
                try
                {
                    await qdrantStore.DeleteByDocumentIdAsync(qdrantCollectionName, document.Id, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "[DOC-INGEST] Qdrant pre-cleanup failed for DocumentId={DocumentId} — continuing",
                        document.Id);
                }
            }

            var embeddingEntities = new List<DocumentChunkEmbedding>();
            var chunkList         = chunks.ToList();

            for (var i = 0; i < chunkList.Count; i++)
            {
                var chunk  = chunkList[i];
                var result = embeddings.FirstOrDefault(e => e.Index == i);
                if (result is null || result.Vector.Length == 0) continue;

                embeddingEntities.Add(new DocumentChunkEmbedding
                {
                    Id                = Guid.NewGuid(),
                    DocumentChunkId   = chunk.Id,
                    EmbeddingProvider = providerType,
                    Model             = modelId,
                    VectorJson        = System.Text.Json.JsonSerializer.Serialize(result.Vector),
                    Dimensions        = result.Vector.Length,
                    CreatedUtc        = DateTime.UtcNow
                });

                if (qdrantCollectionName is not null)
                {
                    try
                    {
                        var payload = new QdrantChunkPayload
                        {
                            DocumentChunkId   = chunk.Id,
                            DocumentId        = document.Id,
                            UserId            = document.UserId,
                            FileId            = document.FileId,
                            FileName          = job.FileName,
                            DocumentTitle     = document.Title,
                            ChunkIndex        = chunk.ChunkIndex,
                            PageNumber        = chunk.PageNumber,
                            Heading           = chunk.Heading,
                            EmbeddingModel    = modelId,
                            EmbeddingProvider = providerType,
                            ContentPreview    = chunk.Content.Length > 500
                                ? chunk.Content[..500]
                                : chunk.Content,
                            CreatedUtc        = DateTime.UtcNow
                        };
                        await qdrantStore.UpsertAsync(qdrantCollectionName, chunk.Id, result.Vector, payload, ct);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex,
                            "[DOC-INGEST] Qdrant upsert failed for chunk {Index} — MySQL record still saved",
                            chunk.ChunkIndex);
                    }
                }
            }

            db.DocumentChunkEmbeddings.AddRange(embeddingEntities);

            // ── Ready ────────────────────────────────────────────────────────────

            var durationMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;

            document.StatusEnum              = DocumentStatus.Ready;
            document.ProcessingStage         = null;
            document.ProgressCurrent         = chunks.Count;
            document.ProgressTotal           = chunks.Count;
            document.ProgressPercent         = 100;
            document.ProcessingCompletedAt   = DateTime.UtcNow;
            document.ProcessingLockId        = null;
            document.ProcessingLockExpiresAt = null;
            document.RagIndexReady           = true;
            document.UpdatedUtc              = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "[DOC-INGEST] DocumentId={DocumentId} Status=Ready DurationMs={Ms} Chunks={Chunks} Embeddings={Emb}",
                job.DocumentId, durationMs, chunks.Count, embeddingEntities.Count);

            await NotifyAsync(signalR, document, job.UserId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "[DOC-INGEST-FAILED] DocumentId={DocumentId} Stage={Stage} Attempt={Attempt}",
                job.DocumentId, document.ProcessingStage, document.ProcessingAttemptCount);

            await SetFailedAsync(db, document, ex.Message, ct);
            await NotifyAsync(signalR, document, job.UserId, ct);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task SetStageAsync(
        InfinityAI.Data.ApplicationDbContext db,
        Document doc,
        DocumentStatus status,
        string stage,
        CancellationToken ct)
    {
        doc.StatusEnum       = status;
        doc.ProcessingStage  = stage;
        doc.UpdatedUtc       = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static async Task SetFailedAsync(
        InfinityAI.Data.ApplicationDbContext db,
        Document doc,
        string error,
        CancellationToken ct)
    {
        doc.StatusEnum              = DocumentStatus.Failed;
        doc.ErrorMessage            = error;
        doc.ProcessingCompletedAt   = DateTime.UtcNow;
        doc.ProcessingLockId        = null;
        doc.ProcessingLockExpiresAt = null;
        doc.UpdatedUtc              = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static async Task NotifyAsync(
        SignalRNotificationClient signalR,
        Document doc,
        Guid? userId,
        CancellationToken ct)
    {
        try
        {
            var msg = new DocumentProcessingProgressMessage
            {
                DocumentId   = doc.Id,
                FileId       = doc.FileId,
                FileName     = doc.Title,
                Status       = doc.Status,
                Stage        = doc.ProcessingStage ?? doc.Status,
                Current      = doc.ProgressCurrent,
                Total        = doc.ProgressTotal,
                Percent      = doc.ProgressPercent,
                ErrorMessage = doc.ErrorMessage ?? doc.BlockReason
            };
            await signalR.SendDocumentProgressAsync(userId, msg, ct);
        }
        catch
        {
            // SignalR failures must never abort the ingestion pipeline
        }
    }
}
