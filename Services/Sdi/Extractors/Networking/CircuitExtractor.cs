using InfinityAI.Api.Models.Rag;
using InfinityAI.Api.Models.Sdi;

namespace InfinityAI.Api.Services.Sdi.Extractors.Networking;

/// <summary>
/// Extracts circuit/connection statistics from network inventory spreadsheets.
/// Extends ColumnNormalizer's CIRCUIT concept with additional telecom aliases:
/// CID, CircuitId, ServiceId, ConnectionId, ServiceRef, OrderId.
/// </summary>
public sealed class CircuitExtractor : IStructuredValueExtractor
{
    public string Name     => "CircuitExtractor";
    public string Version  => "1.0";
    public int    Priority => 1000;

    private static readonly string[] QueryTerms =
    [
        "circuit", "krets", "cid", "service id", "serviceid",
        "connection", "förbindelse", "link", "order id", "orderid"
    ];

    // Extended column aliases beyond what ColumnNormalizer knows
    private static readonly string[] CircuitColumnAliases =
    [
        "circuit", "circuit id", "circuitid", "circuit_id",
        "cid", "krets",
        "service id", "serviceid", "service_id",
        "connection id", "connectionid", "connection_id",
        "service ref", "serviceref", "service_ref",
        "order id", "orderid", "order_id",
        "link id", "linkid", "link_id",
        "förbindelse"
    ];

    public bool CanHandle(QueryIntent intent, StructuredDocument document, string userQuery) =>
        (intent is QueryIntent.Count or QueryIntent.DistinctCount)
        && QueryTerms.Any(t => userQuery.ToLowerInvariant().Contains(t));

    public StructuredAnswer? Extract(QueryIntent intent, StructuredDocument document, string userQuery)
    {
        try
        {
            var column = FindCircuitColumn(document.Tables, userQuery);
            if (column is null) return null;

            var totalRows = document.TotalRows;
            var all       = TableHelpers.GetColumnValues(document.Tables, column)
                                .Where(v => !string.IsNullOrWhiteSpace(v))
                                .ToList();
            var distinct  = all.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var (value, method) = intent switch
            {
                QueryIntent.DistinctCount => (distinct.Count.ToString(), $"Distinct({ColumnNormalizer.Normalize(column)})"),
                QueryIntent.Count         => (all.Count.ToString(),      $"Count({ColumnNormalizer.Normalize(column)})"),
                _                         => (null, null)
            };

            if (value is null) return null;

            return new StructuredAnswer
            {
                Value           = value,
                Confidence      = 100,
                Method          = method!,
                RowsProcessed   = totalRows,
                ColumnUsed      = column,
                IsExact         = true,
                ColumnMatched   = true,
                ValuesExtracted = all.Count,
                DistinctValues  = distinct.Count,
                FirstValues     = string.Join(",", distinct.Take(10))
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? FindCircuitColumn(List<ParsedTable> tables, string userQuery)
    {
        // Standard concept matching handles "circuit", "krets", "connection" etc.
        var standard = ColumnTargetExtractor.FindTargetColumn(tables, userQuery);
        if (standard is not null) return standard;

        // Extended alias matching for telecom-specific column names
        var allColumns = tables.SelectMany(t => t.Columns)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return allColumns.FirstOrDefault(c =>
            CircuitColumnAliases.Any(alias =>
                string.Equals(
                    c.Replace("_", " ").Replace("-", " ").Trim(),
                    alias.Replace("_", " ").Replace("-", " ").Trim(),
                    StringComparison.OrdinalIgnoreCase)));
    }
}
