using System.Globalization;

namespace InfinityAI.Api.Services.Sdi;

/// <summary>
/// Shared utilities for reading column values and parsing numerics from parsed tables.
/// Used by all extractor implementations.
/// </summary>
internal static class TableHelpers
{
    internal static IEnumerable<string> GetColumnValues(List<ParsedTable> tables, string columnName)
    {
        foreach (var table in tables)
        {
            var idx = ColumnTargetExtractor.FindColumnIndex(table, columnName);
            if (idx < 0) continue;

            foreach (var row in table.Rows)
            {
                if (idx < row.Length)
                    yield return row[idx];
            }
        }
    }

    internal static List<double> GetNumericValues(List<ParsedTable> tables, string columnName)
    {
        var result = new List<double>();

        foreach (var raw in GetColumnValues(tables, columnName))
        {
            if (TryParseNumeric(raw, out var d))
                result.Add(d);
        }

        return result;
    }

    /// <summary>
    /// Parses a cell value as a number, handling unit suffixes (G/M/K/T/bps),
    /// European comma decimals, and thousands separators.
    /// Strips but does NOT scale unit suffixes — 100M and 1G both return their numeric part.
    /// Use <see cref="TryParseBandwidthMbps"/> when unit scaling is needed.
    /// </summary>
    internal static bool TryParseNumeric(string value, out double result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = 0;
            return false;
        }

        var clean = value.Replace(" ", "").TrimEnd('G', 'M', 'K', 'T', 'b', 'B', 'p', 's');

        if (clean.Contains(','))
        {
            var dotIdx   = clean.IndexOf('.');
            var commaIdx = clean.IndexOf(',');

            if (dotIdx < 0 && clean.Count(c => c == ',') == 1)
                clean = clean.Replace(',', '.');
            else if (dotIdx > commaIdx)
                clean = clean.Replace(",", "");
        }

        return double.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out result);
    }

    /// <summary>
    /// Parses a bandwidth value and normalises it to Mbps.
    /// Examples: "1G" → 1000, "100M" → 100, "10G" → 10000, "500" → 500 (assumed Mbps).
    /// </summary>
    internal static bool TryParseBandwidthMbps(string value, out double mbps)
    {
        if (string.IsNullOrWhiteSpace(value)) { mbps = 0; return false; }

        var v = value.Trim();

        // Detect trailing unit: G/g, M/m, T/t, K/k (Gbps, Mbps, Tbps, Kbps)
        double multiplier = 1.0;
        if (v.EndsWith("G", StringComparison.OrdinalIgnoreCase))      { multiplier = 1_000;     v = v[..^1]; }
        else if (v.EndsWith("T", StringComparison.OrdinalIgnoreCase)) { multiplier = 1_000_000; v = v[..^1]; }
        else if (v.EndsWith("M", StringComparison.OrdinalIgnoreCase)) { multiplier = 1;         v = v[..^1]; }
        else if (v.EndsWith("K", StringComparison.OrdinalIgnoreCase)) { multiplier = 0.001;     v = v[..^1]; }

        // Strip remaining unit tail (bps, BPS, bit, etc.)
        v = v.TrimEnd('b', 'B', 'p', 'P', 's', 'S', 'i', 't').Trim();

        if (!double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var num))
        {
            mbps = 0;
            return false;
        }

        mbps = num * multiplier;
        return true;
    }

    /// <summary>
    /// Formats a double result: no decimal places when the value is a whole number.
    /// </summary>
    internal static string FormatResult(double value) =>
        value == Math.Floor(value)
            ? ((long)value).ToString()
            : value.ToString("F2", CultureInfo.InvariantCulture);
}
