using ClosedXML.Excel;
using InfinityAI.Api.Helpers;
using InfinityAI.Api.Models.Database;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.RegularExpressions;

namespace InfinityAI.Api.Services
{
    public sealed class DocumentExtractionService : IDocumentExtractionService
    {
        private readonly ILogger<DocumentExtractionService> _logger;

        public DocumentExtractionService(ILogger<DocumentExtractionService> logger)
        {
            _logger = logger;
        }

        public async Task<string> ExtractTextAsync(
            ApplicationFile file,
            CancellationToken ct)
        {
            var extension = file.FileExtension.ToLowerInvariant();

            if (EndpointHelpers.IsPlainTextExtension(extension))
                return await ReadAsUtf8TextAsync(file.StoragePath, ct);

            return extension switch
            {
                ".txt" or ".md" => await File.ReadAllTextAsync(file.StoragePath, ct),
                ".pdf"          => ExtractPdf(file.StoragePath),
                ".docx"         => ExtractDocx(file.StoragePath),
                ".xlsx" or ".xlsm" => ExtractXlsx(file.StoragePath),
                ".csv"          => await File.ReadAllTextAsync(file.StoragePath, ct),
                // TODO: Add extraction for .xls (legacy binary format) — requires NPOI or ExcelDataReader
                _ => throw new NotSupportedException($"Document type {extension} is not supported.")
            };
        }

        private static async Task<string> ReadAsUtf8TextAsync(string path, CancellationToken ct)
        {
            var bytes = await File.ReadAllBytesAsync(path, ct);
            try
            {
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return Encoding.Latin1.GetString(bytes);
            }
        }

        private static string ExtractPdf(string path)
        {
            using var document = UglyToad.PdfPig.PdfDocument.Open(path);

            var sb = new StringBuilder();

            foreach (var page in document.GetPages())
            {
                sb.AppendLine($"--- Page {page.Number} ---");

                var words = page.GetWords()
                    .OrderByDescending(w => w.BoundingBox.Bottom)
                    .ThenBy(w => w.BoundingBox.Left)
                    .ToList();

                var lines = new List<List<UglyToad.PdfPig.Content.Word>>();

                const double yTolerance = 3.0;

                foreach (var word in words)
                {
                    var line = lines.FirstOrDefault(l =>
                        Math.Abs(l[0].BoundingBox.Bottom - word.BoundingBox.Bottom) < yTolerance);

                    if (line is null)
                        lines.Add([word]);
                    else
                        line.Add(word);
                }

                foreach (var line in lines)
                {
                    var orderedLine = line
                        .OrderBy(w => w.BoundingBox.Left)
                        .ToList();

                    var lineSb = new StringBuilder();
                    var previousRight = 0.0;

                    foreach (var word in orderedLine)
                    {
                        var gap = word.BoundingBox.Left - previousRight;

                        if (lineSb.Length > 0)
                        {
                            if (gap > 20)
                                lineSb.Append(" | ");
                            else
                                lineSb.Append(' ');
                        }

                        lineSb.Append(word.Text);
                        previousRight = word.BoundingBox.Right;
                    }

                    sb.AppendLine(FixFortinetModelNames(lineSb.ToString()));
                }

                sb.AppendLine();
            }

            return CleanupPdfArtifacts(FixFortinetModelNames(sb.ToString()));
        }

        private static string FixFortinetModelNames(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            text = Regex.Replace(text, @"\b(FG|FWF)-(\d+)\s+([A-Z])\b", "$1-$2$3");
            text = Regex.Replace(text, @"\b(FG/FWF)-(\d+)\s+([A-Z])\b", "$1-$2$3");

            return text;
        }

        // PDF-only: fixes OCR run-together artifacts like "10Gbps" → "10 Gbps".
        // Must NOT be applied to Excel or CSV data — it breaks structured identifiers.
        internal static string CleanupPdfArtifacts(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            text = Regex.Replace(text, @"(\d)([A-Za-z])", "$1 $2");
            text = Regex.Replace(text, @"Gbps(\d)", "Gbps $1");
            text = Regex.Replace(text, @"Mbps(\d)", "Mbps $1");
            text = Regex.Replace(text, @"([A-Za-z])(\d)([A-Za-z])", "$1 $2 $3");
            text = Regex.Replace(text, @" {2,}", " ");

            return text.Trim();
        }

        private static string ExtractDocx(string path)
        {
            using var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(path, false);
            return doc.MainDocumentPart?.Document?.Body?.InnerText ?? "";
        }

        private static readonly Regex FntPattern =
            new(@"FNT\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private string ExtractXlsx(string path)
        {
            using var workbook = new XLWorkbook(path);
            var sb = new StringBuilder();

            foreach (var sheet in workbook.Worksheets)
            {
                // Use RangeUsed() instead of RowsUsed():
                //   - RangeUsed() returns the minimal bounding box of all used cells and lets
                //     us iterate every row number in that range, including rows that RowsUsed()
                //     skips because their only content is in a merged cell master elsewhere.
                //   - We then resolve merged cells explicitly so non-master cells in a merge
                //     group produce the shared value rather than an empty string.
                var usedRange = sheet.RangeUsed();
                if (usedRange == null) continue;

                var firstRow = usedRange.RangeAddress.FirstAddress.RowNumber;
                var lastRow  = usedRange.RangeAddress.LastAddress.RowNumber;
                var firstCol = usedRange.RangeAddress.FirstAddress.ColumnNumber;
                var lastCol  = usedRange.RangeAddress.LastAddress.ColumnNumber;

                var colNums = Enumerable.Range(firstCol, lastCol - firstCol + 1).ToList();

                sb.AppendLine($"--- Sheet: {sheet.Name} ---");

                // Detect how many header rows exist: sheets with merged cells spanning
                // multiple columns have "group header" rows above the leaf column-name row.
                // For a 2-row header like [ Nätbrygga | Primary | Secondary ] / [ Vlan | Network | … ],
                // we combine them into qualified names like "Nätbrygga_Vlan", "Primary_VLAN_1".
                var headerRowCount = DetectHeaderRowCount(sheet, firstRow, lastRow, firstCol, lastCol);
                var headerRows     = Enumerable.Range(firstRow, headerRowCount).ToList();

                List<string> headers;
                if (headerRowCount == 1)
                {
                    headers = colNums
                        .Select(col => EscapeMarkdownCell(ReadCell(sheet, firstRow, col)))
                        .ToList();
                }
                else
                {
                    headers = BuildQualifiedHeaders(sheet, headerRows, colNums);
                }

                sb.AppendLine("| " + string.Join(" | ", headers) + " |");
                sb.AppendLine("| " + string.Join(" | ", headers.Select(h => new string('-', Math.Max(3, h.Length)))) + " |");

                var dataStart = firstRow + headerRowCount;
                var dataRows  = 0;
                var fntTotal  = 0;
                string? firstDataRow = null;
                string  lastDataRow  = "";

                for (var rowNum = dataStart; rowNum <= lastRow; rowNum++)
                {
                    var cells   = colNums.Select(col => EscapeMarkdownCell(ReadCell(sheet, rowNum, col))).ToList();
                    var rowText = "| " + string.Join(" | ", cells) + " |";
                    sb.AppendLine(rowText);

                    dataRows++;
                    fntTotal += FntPattern.Matches(rowText).Count;
                    firstDataRow ??= rowText;
                    lastDataRow   = rowText;
                }

                _logger.LogInformation(
                    "[EXTRACT_XLSX] File='{Path}' Sheet='{Sheet}' HeaderRows={HdrRows} DataRows={Rows} Cols={Cols} " +
                    "FntMatches={Fnt} Headers=[{Headers}]",
                    Path.GetFileName(path), sheet.Name, headerRowCount, dataRows, colNums.Count, fntTotal,
                    string.Join(", ", headers.Where(h => h.Length > 0).Take(12)));

                if (firstDataRow != null)
                {
                    _logger.LogDebug(
                        "[EXTRACT_XLSX_SAMPLE] Sheet='{Sheet}' First='{First}' Last='{Last}'",
                        sheet.Name, firstDataRow, lastDataRow);
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }

        // Counts how many leading rows form the header block.
        // A "group header" row has at least one merged range that spans multiple columns —
        // these are the top-level category rows (e.g., "Primary", "Secondary").
        // The final header row (leaf column names) typically has no such horizontal merge.
        // Returns at least 1 (single header row is the minimum).
        private static int DetectHeaderRowCount(
            IXLWorksheet sheet, int firstRow, int lastRow, int firstCol, int lastCol)
        {
            var groupRows = 0;

            for (var row = firstRow; row <= Math.Min(firstRow + 3, lastRow - 1); row++)
            {
                if (HasHorizontalMerge(sheet, row, firstCol, lastCol))
                    groupRows++;
                else
                    break;
            }

            // +1 accounts for the leaf column-name row that follows the group rows
            return groupRows + 1;
        }

        // Returns true when the given row contains at least one merged range that spans
        // two or more columns (a horizontal/group merge, not a vertical single-column merge).
        private static bool HasHorizontalMerge(
            IXLWorksheet sheet, int row, int firstCol, int lastCol)
        {
            return sheet.MergedRanges.Any(mr =>
            {
                var fa = mr.RangeAddress.FirstAddress;
                var la = mr.RangeAddress.LastAddress;
                return fa.RowNumber == row
                    && fa.ColumnNumber >= firstCol
                    && la.ColumnNumber <= lastCol
                    && la.ColumnNumber > fa.ColumnNumber;
            });
        }

        // Builds qualified column names by concatenating the value from each header row.
        // E.g. group row "Primary" + leaf row "VLAN" → "Primary_VLAN".
        // Duplicates that arise from the same leaf name appearing under the same group
        // (e.g., two "VLAN" columns under "Primary") get a numeric suffix: _1, _2, …
        private static List<string> BuildQualifiedHeaders(
            IXLWorksheet sheet, List<int> headerRows, List<int> colNums)
        {
            var rawNames = new List<string>();

            foreach (var col in colNums)
            {
                var parts = new List<string>();

                foreach (var row in headerRows)
                {
                    var val = ReadCell(sheet, row, col).Trim();

                    // Skip empty values and values already present (vertical merges repeat the
                    // group name across multiple rows — we only need it once per qualified name).
                    if (!string.IsNullOrWhiteSpace(val) &&
                        !parts.Any(p => string.Equals(p, val, StringComparison.OrdinalIgnoreCase)))
                    {
                        parts.Add(val);
                    }
                }

                var name = string.Join("_", parts).Trim('_');
                rawNames.Add(string.IsNullOrWhiteSpace(name) ? "" : name);
            }

            // Count occurrences of each name so duplicates can be suffixed _1, _2, …
            var totalCounts = rawNames
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            var occurrenceSoFar = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var result          = new List<string>();

            for (var i = 0; i < rawNames.Count; i++)
            {
                var name = rawNames[i];

                if (string.IsNullOrWhiteSpace(name))
                {
                    result.Add(EscapeMarkdownCell($"Col{i + 1}"));
                    continue;
                }

                if (totalCounts.TryGetValue(name, out var total) && total > 1)
                {
                    occurrenceSoFar.TryGetValue(name, out var occ);
                    occurrenceSoFar[name] = occ + 1;
                    result.Add(EscapeMarkdownCell($"{name}_{occ + 1}"));
                }
                else
                {
                    result.Add(EscapeMarkdownCell(name));
                }
            }

            return result;
        }

        // Returns the formatted string value of a cell, resolving merged ranges so that
        // any cell within a merged group returns the master cell's value rather than empty.
        private static string ReadCell(IXLWorksheet sheet, int row, int col)
        {
            var cell  = sheet.Cell(row, col);
            var value = cell.GetFormattedString();
            if (!string.IsNullOrEmpty(value)) return value;
            if (!cell.IsMerged()) return value;

            foreach (var mergedRange in sheet.MergedRanges)
            {
                var fa = mergedRange.RangeAddress.FirstAddress;
                var la = mergedRange.RangeAddress.LastAddress;
                if (row >= fa.RowNumber && row <= la.RowNumber &&
                    col >= fa.ColumnNumber && col <= la.ColumnNumber)
                {
                    return mergedRange.FirstCell().GetFormattedString();
                }
            }

            return value;
        }

        private static string EscapeMarkdownCell(string? value) =>
            (value ?? "").Replace("|", "\\|").Replace("\n", " ").Replace("\r", "").Trim();
    }
}
