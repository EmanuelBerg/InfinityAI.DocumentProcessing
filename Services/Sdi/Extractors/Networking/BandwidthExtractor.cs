using InfinityAI.Api.Models.Rag;
using InfinityAI.Api.Models.Sdi;

namespace InfinityAI.Api.Services.Sdi.Extractors.Networking;

/// <summary>
/// Extracts bandwidth aggregations (average, max, min, sum) from network inventory spreadsheets.
/// Normalises mixed unit values to Mbps before computing (100M → 100, 1G → 1000, 10G → 10000).
/// </summary>
public sealed class BandwidthExtractor : IStructuredValueExtractor
{
    public string Name     => "BandwidthExtractor";
    public string Version  => "1.0";
    public int    Priority => 1000;

    private static readonly string[] QueryTerms =
    [
        "bandwidth", "bandbredd", "speed", "hastighet",
        "throughput", "kapacitet", "datarate", "rate"
    ];

    public bool CanHandle(QueryIntent intent, StructuredDocument document, string userQuery) =>
        (intent is QueryIntent.Sum or QueryIntent.Average or QueryIntent.Maximum or QueryIntent.Minimum)
        && QueryTerms.Any(t => userQuery.ToLowerInvariant().Contains(t));

    public StructuredAnswer? Extract(QueryIntent intent, StructuredDocument document, string userQuery)
    {
        try
        {
            var column = ColumnTargetExtractor.FindTargetColumn(document.Tables, userQuery);
            if (column is null) return null;

            var totalRows = document.TotalRows;
            var values    = GetBandwidthValues(document.Tables, column);
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
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses bandwidth values with unit normalisation to Mbps.
    /// Falls back to plain numeric parsing if unit-aware parsing yields nothing.
    /// </summary>
    private static List<double> GetBandwidthValues(List<ParsedTable> tables, string column)
    {
        var mbpsValues = new List<double>();
        var hasUnits   = false;

        foreach (var raw in TableHelpers.GetColumnValues(tables, column))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            // Check if the cell contains a unit suffix (G, M, T, K)
            var trimmed = raw.Trim();
            var lastChar = trimmed.Length > 0 ? char.ToUpperInvariant(trimmed[^1]) : '\0';

            if (lastChar is 'G' or 'M' or 'T' or 'K')
                hasUnits = true;

            if (TableHelpers.TryParseBandwidthMbps(raw, out var mbps))
                mbpsValues.Add(mbps);
        }

        // If no unit-suffixed values were found, fall back to plain numerics
        // (all values assumed to be in a consistent unit already)
        if (!hasUnits)
        {
            var plain = TableHelpers.GetNumericValues(tables, column);
            return plain.Count > mbpsValues.Count ? plain : mbpsValues;
        }

        return mbpsValues;
    }
}
