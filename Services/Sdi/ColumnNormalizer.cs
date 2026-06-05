namespace InfinityAI.Api.Services.Sdi;

/// <summary>
/// Maps raw column names to canonical concept names and resolves aliases.
/// Used by <see cref="ColumnTargetExtractor"/> to bridge the gap between
/// user vocabulary ("how many VLANs") and column headers ("VLAN_ID", "VID").
/// </summary>
public static class ColumnNormalizer
{
    // canonical → aliases (all stored as flattened uppercase, no separators)
    private static readonly (string Canonical, string[] Aliases)[] AliasTable =
    [
        ("VLAN",       ["VLAN", "VLANID", "VID", "VLANNR", "VLANNUMMER", "VLAN ID", "VLAN NR"]),
        ("BANDWIDTH",  ["BANDWIDTH", "BW", "SPEED", "CAPACITY", "THROUGHPUT", "BANDBREDD", "HASTIGHET",
                        "DATARATE", "RATE", "LINK SPEED"]),
        ("SITE",       ["SITE", "LOCATION", "PLATS", "ORT", "STAD", "CITY", "REGION", "LOCATION NAME"]),
        ("DEVICE",     ["DEVICE", "DEVICENAME", "HOSTNAME", "HOST", "EQUIPMENT", "ENHET", "UTRUSTNING",
                        "DEVICE NAME", "HOST NAME"]),
        ("NAME",       ["NAME", "NAMN", "DESCRIPTION", "BESKRIVNING", "LABEL", "ETIKETT", "TITLE",
                        "DESIGNATION", "BETECKNING"]),
        ("SUBNET",     ["SUBNET", "NETWORK", "NÄTVERK", "NATVERK", "IP", "IPADDRESS", "IP ADDRESS",
                        "ADRESS", "PREFIX", "CIDR", "NETWORK ADDRESS"]),
        ("STATUS",     ["STATUS", "STATE", "TILLSTAND", "TILLSTÅND", "CONDITION"]),
        ("INTERFACE",  ["INTERFACE", "INT", "PORT", "GRÄNSSNITT", "GRANSSNITT", "PORT NAME"]),
        ("COUNT",      ["COUNT", "ANTAL", "NUMBER", "NR", "NUM", "QTY", "QUANTITY"]),
        ("ID",         ["ID", "IDENTIFIER", "IDENTIFIERARE", "SERIAL", "SERIALNUMBER"]),
        ("TYPE",       ["TYPE", "TYP", "KIND", "CATEGORY", "KATEGORI", "CLASS", "KLASS"]),
        ("MODEL",      ["MODEL", "MODELL", "PRODUCTMODEL", "PRODUKT", "PRODUCT", "DEVICE MODEL"]),
        ("CIRCUIT",    ["CIRCUIT", "KRETS", "LINK", "CONNECTION", "FÖRBINDELSE"]),
    ];

    private static string Flatten(string s) =>
        s.ToUpperInvariant()
         .Replace(" ", "")
         .Replace("_", "")
         .Replace("-", "")
         .Replace(".", "")
         .Replace("/", "");

    /// <summary>
    /// Returns the canonical concept name for a column header, or the original name if unrecognised.
    /// E.g. "VLAN_ID" → "VLAN", "Bandwidth" → "BANDWIDTH", "UnknownCol" → "UnknownCol".
    /// </summary>
    public static string Normalize(string columnName)
    {
        var flat = Flatten(columnName);

        foreach (var (canonical, aliases) in AliasTable)
        {
            if (flat == Flatten(canonical))
                return canonical;

            foreach (var alias in aliases)
            {
                if (flat == Flatten(alias))
                    return canonical;
            }
        }

        return columnName;
    }

    /// <summary>
    /// Finds the best column from <paramref name="availableColumns"/> that represents
    /// <paramref name="queryConcept"/> (a canonical name or raw alias).
    /// Returns null when no match is found.
    /// </summary>
    public static string? FindBestColumn(IEnumerable<string> availableColumns, string queryConcept)
    {
        var conceptFlat = Flatten(queryConcept);
        var columns     = availableColumns.ToList();

        // 1. Exact match after flattening
        var direct = columns.FirstOrDefault(c => Flatten(c) == conceptFlat);
        if (direct is not null) return direct;

        // 2. Concept matches a canonical alias table entry; find a column in that group
        foreach (var (canonical, aliases) in AliasTable)
        {
            var canonicalFlat = Flatten(canonical);
            var inGroup       = conceptFlat == canonicalFlat
                             || aliases.Any(a => Flatten(a) == conceptFlat);

            if (!inGroup) continue;

            var match = columns.FirstOrDefault(c =>
            {
                var cf = Flatten(c);
                return cf == canonicalFlat || aliases.Any(a => Flatten(a) == cf);
            });

            if (match is not null) return match;
        }

        // 3. Normalized column matches the concept (handles e.g. "VLAN_ID" normalized to "VLAN")
        var normMatch = columns.FirstOrDefault(c => Flatten(Normalize(c)) == conceptFlat);
        return normMatch;
    }
}
