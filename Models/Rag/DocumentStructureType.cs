namespace InfinityAI.Api.Models.Rag;

public enum DocumentStructureType
{
    Unstructured,
    Table,
    Spreadsheet,
    Mixed
}

public static class DocumentStructureClassifier
{
    public static DocumentStructureType Classify(string documentType) =>
        documentType switch
        {
            "Excel" or "Csv" => DocumentStructureType.Spreadsheet,
            _ => DocumentStructureType.Unstructured
        };

    /// <summary>
    /// Refines classification using extracted text when document type alone is insufficient.
    /// A PDF or Word document with predominantly table content is classified as Table.
    /// </summary>
    public static DocumentStructureType ClassifyFromContent(string documentType, string? extractedText)
    {
        var baseType = Classify(documentType);

        if (baseType == DocumentStructureType.Spreadsheet)
            return baseType;

        if (string.IsNullOrWhiteSpace(extractedText))
            return baseType;

        var lines = extractedText.Split('\n');
        var totalLines = lines.Length;

        if (totalLines == 0)
            return baseType;

        var tableLines = lines.Count(l =>
        {
            var t = l.TrimStart();
            return t.StartsWith('|') && t.TrimEnd().EndsWith('|');
        });

        var ratio = (double)tableLines / totalLines;

        if (ratio >= 0.6) return DocumentStructureType.Table;
        if (ratio >= 0.2) return DocumentStructureType.Mixed;

        return baseType;
    }
}
