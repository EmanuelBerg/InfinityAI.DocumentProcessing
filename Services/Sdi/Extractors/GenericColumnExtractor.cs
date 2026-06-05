using InfinityAI.Api.Models.Rag;
using InfinityAI.Api.Models.Sdi;

namespace InfinityAI.Api.Services.Sdi.Extractors;

/// <summary>
/// Domain-agnostic extractor that handles arithmetic on any named column.
/// Runs LAST in the extractor chain — domain-specific extractors take priority.
/// </summary>
public sealed class GenericColumnExtractor : IStructuredValueExtractor
{
    public string Name     => "GenericColumn";
    public string Version  => "1.0";
    public int    Priority => 100;

    public bool CanHandle(QueryIntent intent, StructuredDocument document, string userQuery) =>
        intent is QueryIntent.Count     or QueryIntent.DistinctCount or
                  QueryIntent.Sum       or QueryIntent.Average       or
                  QueryIntent.Maximum   or QueryIntent.Minimum       or
                  QueryIntent.Aggregation;

    public StructuredAnswer? Extract(QueryIntent intent, StructuredDocument document, string userQuery)
    {
        try
        {
            var totalRows = document.TotalRows;

            return intent switch
            {
                QueryIntent.Count or QueryIntent.Aggregation => ExtractCount(document.Tables, totalRows),
                QueryIntent.DistinctCount                    => ExtractDistinct(document.Tables, userQuery, totalRows),
                _                                            => ExtractNumeric(document.Tables, userQuery, totalRows, intent)
            };
        }
        catch
        {
            return null;
        }
    }

    // ── Operations ────────────────────────────────────────────────────────────

    private static StructuredAnswer ExtractCount(List<ParsedTable> tables, int totalRows)
    {
        var sheets = tables
            .Select(t => t.SheetName)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList();

        return new StructuredAnswer
        {
            Value         = totalRows.ToString(),
            Confidence    = 100,
            Method        = "Count(rows)",
            RowsProcessed = totalRows,
            IsExact       = true,
            ColumnMatched = false,
            SheetName     = sheets.Count == 1 ? sheets[0] : null
        };
    }

    private static StructuredAnswer? ExtractDistinct(List<ParsedTable> tables, string userQuery, int totalRows)
    {
        var column = ColumnTargetExtractor.FindTargetColumn(tables, userQuery);
        if (column is null) return null;

        var allValues = TableHelpers.GetColumnValues(tables, column)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();

        var distinct = allValues.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return new StructuredAnswer
        {
            Value           = distinct.Count.ToString(),
            Confidence      = 100,
            Method          = $"Distinct({ColumnNormalizer.Normalize(column)})",
            RowsProcessed   = totalRows,
            ColumnUsed      = column,
            IsExact         = true,
            ColumnMatched   = true,
            ValuesExtracted = allValues.Count,
            DistinctValues  = distinct.Count,
            FirstValues     = string.Join(",", distinct.Take(10))
        };
    }

    private static StructuredAnswer? ExtractNumeric(
        List<ParsedTable> tables, string userQuery, int totalRows, QueryIntent intent)
    {
        var column = ColumnTargetExtractor.FindTargetColumn(tables, userQuery);
        if (column is null) return null;

        var values = TableHelpers.GetNumericValues(tables, column);
        if (values.Count == 0) return null;

        var (result, method) = intent switch
        {
            QueryIntent.Sum     => (values.Sum(),     $"Sum({ColumnNormalizer.Normalize(column)})"),
            QueryIntent.Average => (values.Average(), $"Average({ColumnNormalizer.Normalize(column)})"),
            QueryIntent.Maximum => (values.Max(),     $"Max({ColumnNormalizer.Normalize(column)})"),
            QueryIntent.Minimum => (values.Min(),     $"Min({ColumnNormalizer.Normalize(column)})"),
            _                   => (0.0, "")
        };

        if (string.IsNullOrEmpty(method)) return null;

        return new StructuredAnswer
        {
            Value         = TableHelpers.FormatResult(result),
            Confidence    = 100,
            Method        = method,
            RowsProcessed = totalRows,
            ColumnUsed    = column,
            IsExact       = true,
            ColumnMatched = true
        };
    }
}
