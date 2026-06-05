namespace InfinityAI.Api.Models.Rag;

public sealed class CoverageResult
{
    public int SourceItems { get; set; }
    public int ReturnedItems { get; set; }
    public double CoveragePercent { get; set; }
    public bool Passed { get; set; }

    // "PASS" | "WARNING" | "FAILED" | "N/A"
    public string Status { get; set; } = "N/A";
}
