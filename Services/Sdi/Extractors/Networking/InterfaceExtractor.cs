using System.Text.RegularExpressions;
using InfinityAI.Api.Models.Rag;
using InfinityAI.Api.Models.Sdi;

namespace InfinityAI.Api.Services.Sdi.Extractors.Networking;

/// <summary>
/// Extracts interface/port statistics from network inventory spreadsheets.
/// Supports named interface columns and content-based detection of interface name patterns
/// (Gi, Te, Fa, Eth, xe-, ge-, Po, etc.) when no column header is present.
/// </summary>
public sealed class InterfaceExtractor : IStructuredValueExtractor
{
    public string Name     => "InterfaceExtractor";
    public string Version  => "1.0";
    public int    Priority => 1000;

    private static readonly string[] QueryTerms =
    [
        "interface", "gränssnitt", "port", "ports",
        "gig", "gigabit", "tengig", "fastethernet"
    ];

    private static readonly string[] InterfaceColumnAliases =
    [
        "interface", "gränssnitt", "port", "port name", "portname",
        "int", "intf", "if name", "ifname", "physical port"
    ];

    // Matches common network interface name prefixes
    private static readonly Regex InterfacePattern = new(
        @"^(GigabitEthernet|TenGigabitEthernet|FastEthernet|Ethernet|" +
        @"Gi|Te|Fa|Eth|xe-|ge-|et-|fe-|Po|Port-channel|PortChannel|" +
        @"Loopback|Lo|Vlan|Management|Mgmt|Tunnel|Serial|BVI)\d",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public bool CanHandle(QueryIntent intent, StructuredDocument document, string userQuery) =>
        (intent is QueryIntent.Count or QueryIntent.DistinctCount)
        && QueryTerms.Any(t => userQuery.ToLowerInvariant().Contains(t));

    public StructuredAnswer? Extract(QueryIntent intent, StructuredDocument document, string userQuery)
    {
        try
        {
            var totalRows = document.TotalRows;

            // Try named-column path first
            var column = FindInterfaceColumn(document.Tables, userQuery);
            if (column is not null)
                return ExtractFromColumn(intent, document.Tables, column, totalRows);

            // Fallback: scan all cells for interface name patterns
            return ExtractFromContent(intent, document.Tables, totalRows);
        }
        catch
        {
            return null;
        }
    }

    // ── Column-based path ─────────────────────────────────────────────────────

    private static string? FindInterfaceColumn(List<ParsedTable> tables, string userQuery)
    {
        var standard = ColumnTargetExtractor.FindTargetColumn(tables, userQuery);
        if (standard is not null) return standard;

        var allColumns = tables.SelectMany(t => t.Columns)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return allColumns.FirstOrDefault(c =>
            InterfaceColumnAliases.Any(alias =>
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

    // ── Content-based fallback: detect interface name patterns in cells ────────

    private static StructuredAnswer? ExtractFromContent(
        QueryIntent intent, List<ParsedTable> tables, int totalRows)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in tables)
        {
            foreach (var row in table.Rows)
            {
                foreach (var cell in row)
                {
                    var trimmed = cell.Trim();
                    if (InterfacePattern.IsMatch(trimmed))
                        found.Add(trimmed);
                }
            }
        }

        if (found.Count == 0) return null;

        var (value, method) = intent switch
        {
            QueryIntent.DistinctCount => (found.Count.ToString(), "Distinct(INTERFACE_FROM_CONTENT)"),
            QueryIntent.Count         => (found.Count.ToString(), "Count(INTERFACE_FROM_CONTENT)"),
            _                         => (null, null)
        };

        if (value is null) return null;

        return new StructuredAnswer
        {
            Value             = value,
            Confidence        = 95,
            Method            = method!,
            RowsProcessed     = totalRows,
            ColumnUsed        = "Interface(content-scan)",
            IsExact           = false,
            ColumnMatched     = false,
            FallbackExtractor = "InterfaceContentScan",
            DistinctValues    = found.Count,
            FirstValues       = string.Join(",", found.OrderBy(x => x).Take(10))
        };
    }
}
