using InfinityAI.Data;
using InfinityAI.Pipeline.Contracts;
using Microsoft.EntityFrameworkCore;

namespace InfinityAI.Api.Pipeline
{
    public sealed class PipelineOrchestrator(
        IHttpClientFactory httpClientFactory,
        ApplicationDbContext db,
        ILogger<PipelineOrchestrator> logger)
    {
        public async Task<PipelineExecutionResult> ExecuteAsync(
            PipelineRequest request,
            CancellationToken ct)
        {
            var steps = await db.PipelineComponentSettings
                .Where(x => x.Enabled)
                .OrderBy(x => x.Order)
                .ToListAsync(ct);

            foreach (var step in steps.Where(x => x.Enabled).OrderBy(x => x.Order))
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(step.TimeoutSeconds));

                var client = httpClientFactory.CreateClient();

                logger.LogInformation(
                    "Executing pipeline step {StepName} for request {RequestId}",
                    step.Name,
                    request.RequestId);

                var response = await client.PostAsJsonAsync(step.Url, request, timeoutCts.Token);

                response.EnsureSuccessStatusCode();

                var pipelineResponse =
                    await response.Content.ReadFromJsonAsync<PipelineResponse>(
                        cancellationToken: timeoutCts.Token);

                if (pipelineResponse is null)
                {
                    return PipelineExecutionResult.Error(
                        request.RequestId,
                        step.Name,
                        "Pipeline component returned empty response.");
                }

                if (pipelineResponse.Action is PipelineAction.Block or PipelineAction.Error)
                {
                    return PipelineExecutionResult.Stopped(pipelineResponse);
                }
            }

            return PipelineExecutionResult.Continue(request.RequestId);
        }
    }
}
