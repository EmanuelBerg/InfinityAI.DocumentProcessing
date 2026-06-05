using System.Text.RegularExpressions;
using InfinityAI.Api.Models.Rag;
using InfinityAI.Api.Models.Sdi;

namespace InfinityAI.Api.Services.Sdi.Extractors.Networking;

/// <summary>
/// Extracts IP address statistics from spreadsheet data.
/// Supports named IP columns and content-based IPv4 detection for tables
/// where addresses are embedded in cells without a dedicated column header.
/// </summary>
public sealed class IpAddressExtractor : IStructuredValueExtractor
{
    public string Name     => "IpAddressExtractor";
    public string Version  => "1.0";
    public int    Priority => 1000;

    private static readonly string[] QueryTerms =
    [
        "ip address", "ip-address", "ip adress", "ip-adress",
        "ipaddress", "ipadress", "ipv4", "ipv6", "nätverksadress"
    ];

    // Matches a.b.c.d where each octet is 0-255
    private static readonly Regex Ipv4Regex = new(
        @"\b(?:(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\.){3}(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\b",
        RegexOptions.Compiled);

    // Known IP/subnet column concepts — extends ColumnNormalizer's SUBNET concept
    private static readonly string[] IpColumnAliases =
    [
        "ip", "ip address", "ip-address", "ipaddress", "ip adress", "ip-adress",
        "ipv4", "ipv6", "address", "adress", "management ip", "mgmt ip",
        "source ip", "destination ip", "loopback"
    ];

    public bool CanHandle(QueryIntent intent, StructuredDocument document, string userQuery) =>
        (intent is QueryIntent.Count or QueryIntent.DistinctCount)
        && QueryTerms.Any(t => userQuery.ToLowerInvariant().Contains(t));

    public StructuredAnswer? Extract(QueryIntent intent, StructuredDocument document, string userQuery)
    {
        try
        {
            var totalRows = document.TotalRows;

            // Try named-column path first
            var column = FindIpColumn(document.Tables, userQuery);
            if (column is not null)
                return ExtractFromColumn(intent, document.Tables, column, totalRows);

            // Fallback: scan all cell values for IPv4 addresses
            return ExtractFromContent(intent, document.Tables, document.RawText, totalRows);
        }
        catch
        {
            return null;
        }
    }

    // ── Column-based path ─────────────────────────────────────────────────────

    private static string? FindIpColumn(List<ParsedTable> tables, string userQuery)
    {
        // Let ColumnTargetExtractor try standard concept matching first
        var standard = ColumnTargetExtractor.FindTargetColumn(tables, userQuery);
        if (standard is not null) return standard;

        // Try extended IP-specific alias list
        var allColumns = tables.SelectMany(t => t.Columns)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return allColumns.FirstOrDefault(c =>
            IpColumnAliases.Any(alias =>
                string.Equals(c.Trim(), alias, StringComparison.OrdinalIgnoreCase)));
    }

    private static StructuredAnswer? ExtractFromColumn(
        QueryIntent intent, List<ParsedTable> tables, string column, int totalRows)
    {
        var all      = TableHelpers.GetColumnValues(tables, column).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        var distinct = all.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

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

    // ── Content-based fallback: scan cells for IPv4 patterns ─────────────────

    private static StructuredAnswer? ExtractFromContent(
        QueryIntent intent, List<ParsedTable> tables, string rawText, int totalRows)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Scan all cell values
        foreach (var table in tables)
        {
            foreach (var row in table.Rows)
            {
                foreach (var cell in row)
                {
                    foreach (Match m in Ipv4Regex.Matches(cell))
                        found.Add(m.Value);
                }
            }
        }

        if (found.Count == 0) return null;

        // Content scan is less certain — apply 95% confidence
        var (value, method) = intent switch
        {
            QueryIntent.DistinctCount => (found.Count.ToString(), "Distinct(IPv4_FROM_CONTENT)"),
            QueryIntent.Count         => (found.Count.ToString(), "Count(IPv4_FROM_CONTENT)"),
            _                         => (null, null)
        };

        if (value is null) return null;

        return new StructuredAnswer
        {
            Value             = value,
            Confidence        = 95,
            Method            = method!,
            RowsProcessed     = totalRows,
            ColumnUsed        = "IPv4(content-scan)",
            IsExact           = false,
            ColumnMatched     = false,
            FallbackExtractor = "IpContentScan",
            DistinctValues    = found.Count,
            FirstValues       = string.Join(",", found.OrderBy(x => x).Take(10))
        };
    }
}
