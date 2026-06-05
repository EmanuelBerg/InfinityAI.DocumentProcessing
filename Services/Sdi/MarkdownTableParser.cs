using System.Text;
using System.Text.RegularExpressions;

namespace InfinityAI.Api.Services.Sdi;

/// <summary>
/// Parses extracted document text (Markdown table format from Excel/CSV extraction,
/// or raw CSV text) into structured <see cref="ParsedTable"/> objects.
/// </summary>
public static class MarkdownTableParser
{
    private static readonly Regex SheetNameRegex =
        new(@"^---\s*Sheet:\s*(.+?)\s*---\s*$", RegexOptions.Compiled);

    private static readonly Regex TableRowRegex =
        new(@"^\|(.+)\|$", RegexOptions.Compiled);

    // A separator row contains only pipes, dashes, colons, and spaces
    private static readonly Regex SeparatorRowRegex =
        new(@"^\|[\s\-\|:]+\|$", RegexOptions.Compiled);

    /// <summary>
    /// Parses Markdown table content (the format produced by ExtractXlsx).
    /// Each "--- Sheet: Name ---" block starts a new table.
    /// If no sheet markers are present, a single table is extracted.
    /// </summary>
    public static List<ParsedTable> Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var tables = new List<ParsedTable>();
        var lines = text.Split('\n', StringSplitOptions.None);

        string? currentSheet = null;
        List<string>? currentHeaders = null;
        List<string[]>? currentRows = null;

        void Flush()
        {
            if (currentHeaders is { Count: > 0 } && currentRows is not null)
            {
                tables.Add(new ParsedTable
                {
                    SheetName = currentSheet ?? "",
                    Columns   = currentHeaders,
                    Rows      = currentRows
                });
            }
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            var sheetMatch = SheetNameRegex.Match(line);
            if (sheetMatch.Success)
            {
                Flush();
                currentSheet  = sheetMatch.Groups[1].Value.Trim();
                currentHeaders = null;
                currentRows    = null;
                continue;
            }

            if (SeparatorRowRegex.IsMatch(line))
                continue;

            var rowMatch = TableRowRegex.Match(line);
            if (!rowMatch.Success)
                continue;

            var cells = SplitMarkdownRow(rowMatch.Groups[1].Value);

            if (currentHeaders is null)
            {
                currentHeaders = cells;
                currentRows    = [];
            }
            else
            {
                var row = PadOrTrim(cells, currentHeaders.Count);
                currentRows!.Add(row);
            }
        }

        Flush();
        return tables;
    }

    /// <summary>
    /// Parses raw CSV/TSV text into a single-sheet table.
    /// Detects delimiter automatically (tab, semicolon, or comma).
    /// </summary>
    public static List<ParsedTable> ParseCsv(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
            return [];

        var delimiter = DetectDelimiter(lines[0]);
        var headers   = SplitCsvLine(lines[0], delimiter);

        if (headers.Count == 0)
            return [];

        var rows = new List<string[]>();

        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            rows.Add(PadOrTrim(SplitCsvLine(line, delimiter), headers.Count));
        }

        return [new ParsedTable { SheetName = "Sheet1", Columns = headers, Rows = rows }];
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static List<string> SplitMarkdownRow(string inner)
    {
        // Temporarily replace escaped pipes to avoid splitting on them, then restore.
        const string placeholder = "\x01";
        return inner
            .Replace("\\|", placeholder)
            .Split('|')
            .Select(c => c.Replace(placeholder, "|").Trim())
            .ToList();
    }

    private static string[] PadOrTrim(List<string> cells, int count)
    {
        var row = new string[count];
        for (var i = 0; i < count; i++)
            row[i] = i < cells.Count ? cells[i] : "";
        return row;
    }

    private static char DetectDelimiter(string line)
    {
        var tabs       = line.Count(c => c == '\t');
        var semicolons = line.Count(c => c == ';');
        var commas     = line.Count(c => c == ',');

        if (tabs >= semicolons && tabs >= commas) return '\t';
        if (semicolons > commas)                  return ';';
        return ',';
    }

    private static List<string> SplitCsvLine(string line, char delimiter)
    {
        var result  = new List<string>();
        var current = new StringBuilder();
        var inQuote = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c == '"')
            {
                if (inQuote && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuote = !inQuote;
                }
            }
            else if (c == delimiter && !inQuote)
            {
                result.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        result.Add(current.ToString().Trim());
        return result;
    }
}
