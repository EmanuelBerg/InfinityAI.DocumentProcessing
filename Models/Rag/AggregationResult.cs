namespace InfinityAI.Api.Models.Rag;

public sealed class AggregationResult
{
    public string FinalAnswer { get; set; } = "";

    /// <summary>Total LLM calls made (extraction passes + optional aggregation pass).</summary>
    public int PassCount { get; set; }

    /// <summary>True when multi-pass recovery was actually needed and executed.</summary>
    public bool Recovered { get; set; }

    public DocumentCoverageResult Coverage { get; set; } = new();
}
