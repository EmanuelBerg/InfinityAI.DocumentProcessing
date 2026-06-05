using System.Text;
using System.Text.Json;
using InfinityAI.Api.Models.Database;
using InfinityAI.Api.Models.SignalR;

namespace InfinityAI.Api.Services;

public sealed class SignalRNotificationClient(
    HttpClient httpClient,
    IConfiguration configuration,
    ILogger<SignalRNotificationClient> logger)
{
    private string BaseUrl => configuration["SignalR__BaseUrl"] ?? "http://infinityai-signalr:8080";
    private string? InternalKey => configuration["SignalR__InternalKey"];

    public async Task NotifyJobUpdatedAsync(MaintenanceJob job, CancellationToken ct = default)
    {
        var payload = new
        {
            jobId         = job.Id,
            jobType       = job.JobType.ToString(),
            status        = job.Status.ToString(),
            startedUtc    = job.StartedUtc,
            completedUtc  = job.CompletedUtc,
            resultSummary = job.ResultSummary,
            errorMessage  = job.ErrorMessage,
            createdUtc    = job.CreatedUtc
        };

        await PostInternalAsync("/internal/maintenance/job-updated", payload, ct);
    }

    public Task SendChatProgressAsync(Guid userId, Guid? conversationId, Guid requestId, string step, string message, string level, CancellationToken ct = default)
    {
        var payload = new { userId, conversationId, requestId, step, message, level, timestamp = DateTimeOffset.UtcNow };
        return PostInternalAsync("/internal/chat/progress", payload, ct);
    }

    public Task SendDocumentProgressAsync(Guid? userId, DocumentProcessingProgressMessage msg, CancellationToken ct = default)
    {
        var payload = new
        {
            userId,
            documentId   = msg.DocumentId,
            fileId       = msg.FileId,
            fileName     = msg.FileName,
            status       = msg.Status,
            stage        = msg.Stage,
            current      = msg.Current,
            total        = msg.Total,
            percent      = msg.Percent,
            errorMessage = msg.ErrorMessage,
            timestamp    = DateTimeOffset.UtcNow
        };
        return PostInternalAsync("/internal/document/progress", payload, ct);
    }

    private async Task PostInternalAsync(string path, object payload, CancellationToken ct)
    {
        try
        {
            var json    = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}{path}");
            request.Content = content;

            if (!string.IsNullOrWhiteSpace(InternalKey))
                request.Headers.Add("X-SignalR-Internal-Key", InternalKey);

            var response = await httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                logger.LogWarning("[SIGNALR] Notification to {Path} returned {Status}", path, response.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[SIGNALR] Failed to notify {Path} — real-time update skipped", path);
        }
    }
}
