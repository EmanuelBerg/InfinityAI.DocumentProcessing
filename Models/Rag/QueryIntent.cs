namespace InfinityAI.Api.Models.Rag;

public enum QueryIntent
{
    SpecificLookup,
    Summary,
    Comparison,
    ListAll,
    TableEnumeration,
    Enumeration,

    // ── Structured Data Intelligence (SDI) ───────────────────────────────────
    Count,
    DistinctCount,
    Sum,
    Average,
    Maximum,
    Minimum,
    Filtering,
    Aggregation
}

public static class QueryIntentClassifier
{
    // TableEnumeration: explicit table row/column scope
    private static readonly string[] TableEnumerationKeywords =
    [
        "lista alla rader", "visa alla rader", "alla poster", "samtliga poster",
        "hela tabellen", "exportera tabellen", "alla kolumner",
        "lista alla produkter och", "visa produkter och", "visa alla produkter och",
        "list all rows", "show all rows", "list all entries", "show all entries",
        "full table", "complete table", "export table"
    ];

    // ── SDI intent keywords ───────────────────────────────────────────────────

    // DistinctCount: must be checked before Count (more specific)
    private static readonly string[] DistinctCountKeywords =
    [
        // Swedish
        "hur många unika", "hur många olika", "antal unika", "antal olika",
        "unika vlan", "unika enheter", "unika modeller", "unika kretsar",
        "unika typer", "distinct", "unique count",
        // English
        "how many unique", "how many different", "how many distinct",
        "unique vlan", "unique devices", "unique models", "unique circuits"
    ];

    // Count: total row count or item count — deliberately specific to avoid false positives
    // on queries like "Hur många portar har FG-60F?" (SpecificLookup about a named product)
    private static readonly string[] CountKeywords =
    [
        // Swedish — specific compound phrases
        "hur många vlan", "hur många rader", "hur många poster",
        "hur många kretsar", "hur många enheter i", "hur många nät",
        "totalt antal", "antal rader", "antal poster",
        "räkna alla", "räkna rader",
        // English — specific compound phrases
        "how many vlan", "how many rows", "how many entries",
        "how many circuits", "how many networks",
        "count all", "count rows", "row count", "total rows", "number of rows",
        "total number of"
    ];

    // Sum: numeric total — "summera" excluded to avoid conflict with SummaryKeywords
    private static readonly string[] SumKeywords =
    [
        // Swedish
        "total bandbredd", "totalt bandbredd", "summa av",
        "total kapacitet", "totalt kapacitet",
        // English
        "total bandwidth", "sum of", "total capacity", "sum bandwidth",
        "aggregate bandwidth"
    ];

    // Average: mean value
    private static readonly string[] AverageKeywords =
    [
        // Swedish
        "genomsnittlig", "genomsnitt", "medelvärde", "medeltal",
        // English
        "average", "mean", "avg"
    ];

    // Maximum: highest value
    private static readonly string[] MaximumKeywords =
    [
        // Swedish
        "högsta", "störst", "maximum",
        // English
        "highest", "maximum", "largest", "max bandwidth", "max speed",
        "highest bandwidth", "highest speed", "highest throughput"
    ];

    // Minimum: lowest value
    private static readonly string[] MinimumKeywords =
    [
        // Swedish
        "lägsta", "minst", "minimum",
        // English
        "lowest", "minimum", "smallest", "min bandwidth", "min speed",
        "lowest bandwidth", "lowest speed"
    ];

    // Filtering: subset selection
    private static readonly string[] FilteringKeywords =
    [
        // Swedish
        "filtrera", "vlan över", "vlan under", "visa rader där",
        // English
        "filter", "show rows where", "where vlan", "above", "below",
        "greater than", "less than", "vlan above", "vlan below"
    ];

    // Aggregation: group-by operations
    private static readonly string[] AggregationKeywords =
    [
        // Swedish
        "gruppera per", "gruppera efter", "per site", "per plats", "antal per",
        // English
        "group by", "breakdown by", "breakdown per",
        "distribution by", "count per"
    ];

    // Enumeration: list/count/inventory — superset of ListAll
    private static readonly string[] EnumerationKeywords =
    [
        // Swedish — listing
        "lista alla", "lista samtliga", "visa alla", "visa samtliga",
        "ge mig hela listan", "ge mig alla", "räkna upp alla",
        // Swedish — models/products/items
        "vilka modeller finns", "vilka produkter finns", "vilka finns det",
        "alla modeller", "samtliga modeller", "alla produkter", "samtliga produkter",
        "vilka modeller förekommer", "vilka produkter förekommer",
        "vilka modeller", "vilka produkter", "vilka enheter",
        // Swedish — requirements
        "lista kraven", "vilka krav", "alla krav", "samtliga krav",
        "lista alla krav", "lista alla modeller", "lista alla produkter",
        // Swedish — inventory
        "inventera", "inventera dokumentet",
        "räkna modeller", "räkna produkter", "räkna krav",
        "hur många modeller", "hur många produkter", "hur många finns",
        "hur många krav", "hur många enheter",
        // English — listing
        "list all", "show all", "list every", "enumerate all",
        "list all models", "list all products", "list all items",
        // English — models/products
        "what models", "which models", "which products", "what products",
        "all models", "all products", "all items",
        // English — counting
        "count models", "count products",
        "how many models", "how many products", "how many items",
        "how many are there",
        // Generic
        "ge mig en lista"
    ];

    // ListAll: retained for backward compatibility; superseded by Enumeration in classifier
    private static readonly string[] ListAllKeywords =
    [
        "lista alla", "lista samtliga", "visa alla", "visa samtliga",
        "ge mig hela listan", "ge mig alla", "räkna upp alla",
        "vilka modeller finns", "vilka produkter finns", "vilka finns det",
        "alla modeller", "samtliga modeller", "alla produkter", "samtliga produkter",
        "vilka modeller", "vilka produkter", "vilka enheter",
        "lista kraven", "vilka krav", "alla krav", "samtliga krav",
        "lista alla krav", "lista alla modeller", "lista alla produkter",
        "list all", "show all", "list every", "enumerate all",
        "what models", "which models", "which products", "what products",
        "ge mig en lista"
    ];

    // Summary: broad document overview
    private static readonly string[] SummaryKeywords =
    [
        "sammanfatta", "summera", "översikt", "vad handlar", "vad innehåller",
        "beskriv dokumentet", "hela dokumentet", "allt i dokumentet",
        "summary", "summarize", "overview", "what does the document", "describe the document"
    ];

    // Comparison: explicitly comparing two or more items
    private static readonly string[] ComparisonKeywords =
    [
        "jämför", "jämförelse", "skillnad", "skillnaden", "jämför mellan",
        "skillnader mellan", "kontra", " vs ", " versus ",
        "compare", "comparison", "difference between", "differences between", "vs."
    ];

    public static QueryIntent Classify(string query)
    {
        var lower = query.ToLowerInvariant();

        // TableEnumeration first: explicit table structure queries
        if (MatchesAny(lower, TableEnumerationKeywords))
            return QueryIntent.TableEnumeration;

        // SDI intents — DistinctCount before Count (more specific)
        if (MatchesAny(lower, DistinctCountKeywords)) return QueryIntent.DistinctCount;
        if (MatchesAny(lower, CountKeywords))         return QueryIntent.Count;
        if (MatchesAny(lower, AverageKeywords))       return QueryIntent.Average;
        if (MatchesAny(lower, MaximumKeywords))       return QueryIntent.Maximum;
        if (MatchesAny(lower, MinimumKeywords))       return QueryIntent.Minimum;
        if (MatchesAny(lower, SumKeywords))           return QueryIntent.Sum;
        if (MatchesAny(lower, AggregationKeywords))   return QueryIntent.Aggregation;
        if (MatchesAny(lower, FilteringKeywords))     return QueryIntent.Filtering;

        // Enumeration: broad item listing/counting — checked before ListAll
        if (MatchesAny(lower, EnumerationKeywords))   return QueryIntent.Enumeration;

        // ListAll: fallback (effectively unreachable for all current keywords)
        if (MatchesAny(lower, ListAllKeywords))       return QueryIntent.ListAll;

        if (MatchesAny(lower, ComparisonKeywords))    return QueryIntent.Comparison;
        if (MatchesAny(lower, SummaryKeywords))       return QueryIntent.Summary;

        return QueryIntent.SpecificLookup;
    }

    /// <summary>True when the intent requires full document retrieval (all chunks, no TopK cutoff).</summary>
    public static bool RequiresFullDocumentRetrieval(QueryIntent intent) =>
        intent is QueryIntent.ListAll
               or QueryIntent.TableEnumeration
               or QueryIntent.Enumeration
               or QueryIntent.Count
               or QueryIntent.DistinctCount
               or QueryIntent.Sum
               or QueryIntent.Average
               or QueryIntent.Maximum
               or QueryIntent.Minimum
               or QueryIntent.Filtering
               or QueryIntent.Aggregation;

    /// <summary>True when the intent should activate extraction-first enumeration prompt mode.</summary>
    public static bool IsEnumerationIntent(QueryIntent intent) =>
        intent is QueryIntent.Enumeration
               or QueryIntent.ListAll
               or QueryIntent.TableEnumeration
               or QueryIntent.Count
               or QueryIntent.DistinctCount;

    /// <summary>True when the intent targets a structured data calculation (SDI path).</summary>
    public static bool IsStructuredDataIntent(QueryIntent intent) =>
        intent is QueryIntent.Count
               or QueryIntent.DistinctCount
               or QueryIntent.Sum
               or QueryIntent.Average
               or QueryIntent.Maximum
               or QueryIntent.Minimum
               or QueryIntent.Filtering
               or QueryIntent.Aggregation;

    private static bool MatchesAny(string lower, string[] keywords) =>
        keywords.Any(kw => lower.Contains(kw));
}
