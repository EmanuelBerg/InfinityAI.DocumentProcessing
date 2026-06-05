using InfinityAI.Pipeline.Contracts;

namespace InfinityAI.Api.Pipeline
{
    public sealed record PipelineExecutionResult
    {
        public string RequestId { get; init; } = "";
        public bool ShouldContinue { get; init; }
        public PipelineResponse? StoppedBy { get; init; }
        public string? ErrorMessage { get; init; }

        public static PipelineExecutionResult Continue(string requestId) =>
            new()
            {
                RequestId = requestId,
                ShouldContinue = true
            };

        public static PipelineExecutionResult Stopped(PipelineResponse response) =>
            new()
            {
                RequestId = response.RequestId,
                ShouldContinue = false,
                StoppedBy = response,
                ErrorMessage = response.Reason
            };

        public static PipelineExecutionResult Error(
            string requestId,
            string component,
            string message) =>
            new()
            {
                RequestId = requestId,
                ShouldContinue = false,
                ErrorMessage = message,
                StoppedBy = new PipelineResponse
                {
                    RequestId = requestId,
                    Component = component,
                    Status = PipelineStatus.Error,
                    Action = PipelineAction.Error,
                    Reason = message
                }
            };
    }
}
