namespace InfinityAI.Api.Services.Sdi;

public sealed class ParsedTable
{
    public string SheetName { get; init; } = "";
    public List<string> Columns { get; init; } = [];
    public List<string[]> Rows { get; init; } = [];
}
