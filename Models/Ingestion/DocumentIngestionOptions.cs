namespace InfinityAI.Api.Models.Ingestion;

public sealed class DocumentIngestionOptions
{
    /// <summary>
    /// Controls where the document ingestion worker runs.
    /// ApiEmbedded  — DocumentIngestionWorker runs inside the API process (default).
    /// ExternalWorker — API only publishes to queue; InfinityAI.Document.Worker process consumes it.
    /// </summary>
    public string Mode                            { get; set; } = "ApiEmbedded";

    public bool   Enabled                         { get; set; } = true;
    public string QueueName                       { get; set; } = "document-ingestion";
    public int    EmbeddingBatchSize              { get; set; } = 32;
    public int    MaxConcurrentEmbeddingRequests  { get; set; } = 2;
    public int    MaxAttempts                     { get; set; } = 3;
    public int    ProcessingLockMinutes           { get; set; } = 30;
    public long   LargeFileThresholdBytes         { get; set; } = 10_485_760; // 10 MB
    public bool   EnableStructuredReadyBeforeEmbeddings { get; set; } = true;
}
