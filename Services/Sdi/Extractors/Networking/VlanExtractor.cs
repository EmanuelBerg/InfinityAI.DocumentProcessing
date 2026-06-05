using System.Text.RegularExpressions;
using InfinityAI.Api.Models.Rag;
using InfinityAI.Api.Models.Sdi;

namespace InfinityAI.Api.Services.Sdi.Extractors.Networking;

/// <summary>
/// Extracts VLAN statistics from spreadsheet data.
/// Supports named VLAN columns (VLAN, VLAN_ID, VID, VLANID) and
/// CLI-style headerless tables where the VLAN ID is the first numeric token per row.
/// Valid VLAN range: 1–4094 (IEEE 802.1Q).
/// </summary>
public sealed class VlanExtractor : IStructuredValueExtractor
{
    public string Name     => "VlanExtractor";
    public string Version  => "1.0";
    public int    Priority => 1000;

    private static readonly string[] QueryTerms =
        ["vlan", "vlans", "vlan-id", "vlanid", "vid"];

    private static readonly Regex FirstTokenRegex =
        new(@"^\s*(\d{1,4})\b", RegexOptions.Compiled);

    private const int VlanMin = 1;
    private const int VlanMax = 4094;

    public bool CanHandle(QueryIntent intent, StructuredDocument document, string userQuery) =>
        (intent is QueryIntent.Count     or QueryIntent.DistinctCount or
                   QueryIntent.Maximum   or QueryIntent.Minimum)
        && QueryTerms.Any(t => userQuery.ToLowerInvariant().Contains(t));

    public StructuredAnswer? Extract(QueryIntent intent, StructuredDocument document, string userQuery)
    {
        try
        {
            var totalRows = document.TotalRows;

            // ── Column-based path (highest confidence) ────────────────────────
            // FindPrimaryVlanTable iterates tables by descending row count and checks
            // both canonical VLAN aliases (VLAN_ID, VID, …) and qualified names that
            // contain "VLAN" when flattened (e.g., "Nätbrygga_Vlan"). This ensures a
            // 268-row Links sheet with column "Nätbrygga_Vlan" beats an 11-row Core
            // sheet with column "VLAN", rather than letting an exact name match on a
            // small summary sheet take priority.
            var (primaryTable, column) = FindPrimaryVlanTable(document.Tables);
            if (primaryTable is not null && column is not null)
                return ExtractFromTable(intent, primaryTable, column, totalRows);

            // ── First-token fallback for headerless CLI-style tables ───────────
            if (!QueryTerms.Any(t => userQuery.ToLowerInvariant().Contains(t)))
                return null;

            var (vlanIds, extracted, rejected) = ScanForVlanIds(document.Tables);
            if (vlanIds.Count == 0) return null;

            return BuildFirstTokenAnswer(intent, vlanIds, extracted, rejected, totalRows);
        }
        catch
        {
            return null;
        }
    }

    // Returns the largest table (by row count) that has a VLAN-related column,
    // checking canonical alias matches first, then qualified names containing "VLAN".
    private static (ParsedTable? Table, string? Column) FindPrimaryVlanTable(List<ParsedTable> tables)
    {
        foreach (var table in tables.OrderByDescending(t => t.Rows.Count))
        {
            // 1. Exact canonical or known alias (VLAN, VLAN_ID, VID, …)
            var canonical = ColumnNormalizer.FindBestColumn(table.Columns, "VLAN");
            if (canonical is not null)
                return (table, canonical);

            // 2. Qualified name whose flattened form contains "VLAN" (e.g., "Nätbrygga_Vlan")
            var qualified = table.Columns
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .FirstOrDefault(c => FlattenCol(c).Contains("VLAN"));
            if (qualified is not null)
                return (table, qualified);
        }
        return (null, null);
    }

    private static string FlattenCol(string s) =>
        s.ToUpperInvariant()
         .Replace(" ", "").Replace("_", "").Replace("-", "").Replace(".", "").Replace("/", "");

    // ── Column-based extraction (single table) ────────────────────────────────

    private static StructuredAnswer? ExtractFromTable(
        QueryIntent intent, ParsedTable table, string column, int totalRows)
    {
        return intent switch
        {
            QueryIntent.DistinctCount => DistinctFromTable(table, column, totalRows),
            QueryIntent.Count         => CountFromTable(table, column, totalRows),
            QueryIntent.Maximum       => NumericFromTable(table, column, totalRows, max: true),
            QueryIntent.Minimum       => NumericFromTable(table, column, totalRows, max: false),
            _                         => null
        };
    }

    private static StructuredAnswer DistinctFromTable(
        ParsedTable table, string column, int totalRows)
    {
        var idx      = ColumnTargetExtractor.FindColumnIndex(table, column);
        var all      = table.Rows.Select(r => idx < r.Length ? r[idx] : "")
                           .Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        var distinct = all.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return new StructuredAnswer
        {
            Value           = distinct.Count.ToString(),
            Confidence      = 100,
            Method          = $"Distinct({ColumnNormalizer.Normalize(column)}) in '{table.SheetName}'",
            RowsProcessed   = totalRows,
            ColumnUsed      = column,
            IsExact         = true,
            ColumnMatched   = true,
            ValuesExtracted = all.Count,
            DistinctValues  = distinct.Count,
            FirstValues     = string.Join(",", distinct.Take(10))
        };
    }

    private static StructuredAnswer CountFromTable(
        ParsedTable table, string column, int totalRows)
    {
        var idx   = ColumnTargetExtractor.FindColumnIndex(table, column);
        var count = table.Rows.Count(r => idx < r.Length && !string.IsNullOrWhiteSpace(r[idx]));

        return new StructuredAnswer
        {
            Value           = count.ToString(),
            Confidence      = 100,
            Method          = $"Count({ColumnNormalizer.Normalize(column)}) in '{table.SheetName}'",
            RowsProcessed   = totalRows,
            ColumnUsed      = column,
            IsExact         = true,
            ColumnMatched   = true,
            ValuesExtracted = count
        };
    }

    private static StructuredAnswer? NumericFromTable(
        ParsedTable table, string column, int totalRows, bool max)
    {
        var idx    = ColumnTargetExtractor.FindColumnIndex(table, column);
        var values = table.Rows
            .Select(r => idx < r.Length ? r[idx] : "")
            .Where(v => TableHelpers.TryParseNumeric(v, out _))
            .Select(v => { TableHelpers.TryParseNumeric(v, out var d); return d; })
            .ToList();

        if (values.Count == 0) return null;

        var result = max ? values.Max() : values.Min();
        var method = max
            ? $"Max({ColumnNormalizer.Normalize(column)}) in '{table.SheetName}'"
            : $"Min({ColumnNormalizer.Normalize(column)}) in '{table.SheetName}'";

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

    // ── First-token fallback ──────────────────────────────────────────────────

    private static StructuredAnswer? BuildFirstTokenAnswer(
        QueryIntent intent,
        HashSet<int> vlanIds,
        int extracted,
        int rejected,
        int totalRows)
    {
        var sorted     = vlanIds.OrderBy(x => x).ToList();
        var firstValues = string.Join(",", sorted.Take(10));

        return intent switch
        {
            QueryIntent.DistinctCount => new StructuredAnswer
            {
                Value             = vlanIds.Count.ToString(),
                Confidence        = 95,
                Method            = "Distinct(VLAN_ID_FROM_FIRST_TOKEN)",
                RowsProcessed     = totalRows,
                ColumnUsed        = "VLAN(first-token)",
                IsExact           = false,
                ColumnMatched     = false,
                FallbackExtractor = "VlanFirstToken",
                ValuesExtracted   = extracted,
                DistinctValues    = vlanIds.Count,
                RejectedRows      = rejected,
                FirstValues       = firstValues
            },
            QueryIntent.Count => new StructuredAnswer
            {
                Value             = extracted.ToString(),
                Confidence        = 95,
                Method            = "Count(VLAN_ROWS_FROM_FIRST_TOKEN)",
                RowsProcessed     = totalRows,
                ColumnUsed        = "VLAN(first-token)",
                IsExact           = false,
                ColumnMatched     = false,
                FallbackExtractor = "VlanFirstToken",
                ValuesExtracted   = extracted
            },
            QueryIntent.Maximum => new StructuredAnswer
            {
                Value             = sorted.Last().ToString(),
                Confidence        = 95,
                Method            = "Max(VLAN_ID_FROM_FIRST_TOKEN)",
                RowsProcessed     = totalRows,
                ColumnUsed        = "VLAN(first-token)",
                IsExact           = false,
                ColumnMatched     = false,
                FallbackExtractor = "VlanFirstToken"
            },
            QueryIntent.Minimum => new StructuredAnswer
            {
                Value             = sorted.First().ToString(),
                Confidence        = 95,
                Method            = "Min(VLAN_ID_FROM_FIRST_TOKEN)",
                RowsProcessed     = totalRows,
                ColumnUsed        = "VLAN(first-token)",
                IsExact           = false,
                ColumnMatched     = false,
                FallbackExtractor = "VlanFirstToken"
            },
            _ => null
        };
    }

    /// <summary>
    /// Scans header cells and data rows for valid VLAN IDs in the leading numeric token.
    /// In headerless tables the parser promotes the first data row to the header,
    /// so we always check header cells to avoid losing the first VLAN.
    /// </summary>
    private (HashSet<int> VlanIds, int Extracted, int Rejected) ScanForVlanIds(List<ParsedTable> tables)
    {
        var vlanIds  = new HashSet<int>();
        int extracted = 0, rejected = 0;

        foreach (var table in tables)
        {
            if (table.Columns.Count > 0)
                Process(table.Columns[0], vlanIds, ref extracted, ref rejected);

            foreach (var row in table.Rows)
            {
                if (row.Length == 0) { rejected++; continue; }
                Process(row[0], vlanIds, ref extracted, ref rejected);
            }
        }

        return (vlanIds, extracted, rejected);
    }

    private void Process(string cell, HashSet<int> ids, ref int extracted, ref int rejected)
    {
        if (string.IsNullOrWhiteSpace(cell)) { rejected++; return; }

        var m = FirstTokenRegex.Match(cell);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var id))
        {
            rejected++;
            return;
        }

        if (id < VlanMin || id > VlanMax) { rejected++; return; }

        ids.Add(id);
        extracted++;
    }
}
