namespace InfinityAI.Api.Models.SignalR;

/// <summary>
/// Pushed via SignalR when a document's ingestion status changes.
/// </summary>
public sealed class DocumentProcessingProgressMessage
{
    public Guid    DocumentId    { get; init; }
    public Guid?   FileId        { get; init; }
    public string  FileName      { get; init; } = "";
    public string  Status        { get; init; } = "";
    public string  Stage         { get; init; } = "";
    public int?    Current       { get; init; }
    public int?    Total         { get; init; }
    public double? Percent       { get; init; }
    public string? ErrorMessage  { get; init; }
}
