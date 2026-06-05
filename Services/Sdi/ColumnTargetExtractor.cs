namespace InfinityAI.Api.Services.Sdi;

/// <summary>
/// Determines which column a user query is targeting.
/// Combines direct name matching, concept heuristics, and canonical alias lookups.
/// </summary>
public static class ColumnTargetExtractor
{
    // Maps Swedish/English query terms → canonical concept name used in ColumnNormalizer
    private static readonly (string[] Terms, string Canonical)[] ConceptMap =
    [
        (["vlan", "vlan-id", "vlanid", "vid"],                            "VLAN"),
        (["bandwidth", "bandbredd", "speed", "hastighet",
          "throughput", "capacity", "kapacitet", "datarate", "rate"],     "BANDWIDTH"),
        (["device", "devices", "enhet", "enheter",
          "equipment", "utrustning", "hostname", "host"],                 "DEVICE"),
        (["site", "sites", "location", "locations",
          "plats", "platser", "city", "region"],                          "SITE"),
        (["subnet", "nätverk", "natverk", "ip", "network",
          "prefix", "cidr"],                                               "SUBNET"),
        (["interface", "port", "ports", "gränssnitt"],                    "INTERFACE"),
        (["circuit", "circuits", "krets", "link", "links"],               "CIRCUIT"),
        (["model", "models", "modell", "modeller"],                       "MODEL"),
        (["status", "state", "tillstånd"],                                "STATUS"),
    ];

    /// <summary>
    /// Finds the best matching actual column name from the parsed tables for the given query.
    /// When multiple tables have a matching column the one from the table with the most data
    /// rows is preferred — this avoids a small summary/Core sheet with 11 rows winning over
    /// a large detail sheet (e.g., Links) with hundreds of rows.
    /// Returns null when no semantic column match is found.
    /// </summary>
    public static string? FindTargetColumn(IEnumerable<ParsedTable> tables, string userQuery)
    {
        var tableList  = tables as IReadOnlyList<ParsedTable> ?? tables.ToList();
        var queryLower = userQuery.ToLowerInvariant();

        // Tables ordered by descending row count so that steps below prefer the
        // most data-rich sheet when the same column name exists in several sheets.
        var orderedTables = tableList
            .OrderByDescending(t => t.Rows.Count)
            .ToList();

        // 1. Column name appears verbatim in the query (case-insensitive).
        // Search tables in row-count order so a large Links table beats a small Core table.
        foreach (var t in orderedTables)
        {
            var directHit = t.Columns
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .FirstOrDefault(c => queryLower.Contains(c.ToLowerInvariant()));
            if (directHit is not null) return directHit;
        }

        // 2. Normalized column name appears in the query
        foreach (var t in orderedTables)
        {
            var normHit = t.Columns
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .FirstOrDefault(c =>
                {
                    var normalized = ColumnNormalizer.Normalize(c);
                    return !string.IsNullOrWhiteSpace(normalized) &&
                           queryLower.Contains(normalized.ToLowerInvariant());
                });
            if (normHit is not null) return normHit;
        }

        // 3. Query contains a known concept term → find the matching column alias.
        // Prefer the matching column from the table with the most rows.
        foreach (var (terms, canonical) in ConceptMap)
        {
            if (!terms.Any(t => queryLower.Contains(t)))
                continue;

            foreach (var table in orderedTables)
            {
                var match = ColumnNormalizer.FindBestColumn(table.Columns, canonical);
                if (match is not null) return match;
            }
        }

        // 4. Column name CONTAINS a known concept alias (handles qualified names like
        // "Nätbrygga_Vlan" or "Primary_VLAN_1"). Prefer the column from the largest table.
        foreach (var (terms, canonical) in ConceptMap)
        {
            if (!terms.Any(t => queryLower.Contains(t)))
                continue;

            var canonicalFlat = Flatten(canonical);

            foreach (var table in orderedTables)
            {
                var containsHit = table.Columns
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .FirstOrDefault(c =>
                    {
                        var cf = Flatten(c);
                        return cf.Contains(canonicalFlat);
                    });

                if (containsHit is not null) return containsHit;
            }
        }

        return null;
    }

    private static string Flatten(string s) =>
        s.ToUpperInvariant()
         .Replace(" ", "")
         .Replace("_", "")
         .Replace("-", "")
         .Replace(".", "")
         .Replace("/", "");

    /// <summary>Returns the 0-based index of the named column in the table, or -1.</summary>
    public static int FindColumnIndex(ParsedTable table, string columnName) =>
        table.Columns.FindIndex(c =>
            string.Equals(c, columnName, StringComparison.OrdinalIgnoreCase));
}
