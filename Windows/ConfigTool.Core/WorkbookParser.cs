using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PairPair.ConfigTool.Core;

internal sealed class WorkbookParser
{
    private sealed record WorkbookSheet(string Name, string RelationshipId);
    private sealed record CellRecord(int Row, int Column, string? Type, int? Style, string Value);

    public List<WorkbookView> Parse(string path)
    {
        var file = new FileInfo(path);
        var baseName = Path.GetFileNameWithoutExtension(path);
        using var archive = ZipFile.OpenRead(path);
        var sharedStrings = ParseSharedStrings(ReadEntry(archive, "xl/sharedStrings.xml", allowMissing: true));
        var sheets = ParseWorkbook(ReadEntry(archive, "xl/workbook.xml"));
        if (sheets.Count == 0) throw new InvalidDataException("工作簿没有可读取的 Sheet");
        var relationships = ParseRelationships(ReadEntry(archive, "xl/_rels/workbook.xml.rels"));
        var dateStyles = ParseDateStyleIndexes(ReadEntry(archive, "xl/styles.xml", allowMissing: true));
        var views = new List<WorkbookView>();

        foreach (var sheet in sheets)
        {
            if (!relationships.TryGetValue(sheet.RelationshipId, out var target))
                throw new InvalidDataException($"无法定位 Sheet：{sheet.Name}");
            target = NormalizeTarget(target);
            var cells = ParseWorksheet(ReadEntry(archive, target));
            var matrix = MakeMatrix(cells, sharedStrings, dateStyles);
            views.Add(new WorkbookView
            {
                Id = $"{Path.GetFileName(path)}::{sheet.Name}",
                Name = baseName,
                FileName = Path.GetFileName(path),
                SheetName = sheet.Name,
                SheetCount = sheets.Count,
                Category = ConfigCategory.NameFor(baseName),
                ModifiedAt = ConfigFiles.IsoTimestamp(file.LastWriteTimeUtc),
                RowCount = matrix.Count,
                ColumnCount = matrix.Count == 0 ? 0 : matrix.Max(row => row.Count),
                Rows = matrix,
                SourceKind = "xlsx",
                SourceSignature = ConfigFiles.Signature(path),
                EditableFromRow = 0,
                LockedCells = [],
                IsLoaded = true
            });
        }
        return views;
    }

    private static string NormalizeTarget(string target)
    {
        if (target.StartsWith('/')) return target[1..];
        return target.StartsWith("xl/", StringComparison.Ordinal) ? target : "xl/" + target.Replace("../", "", StringComparison.Ordinal);
    }

    private static string ReadEntry(ZipArchive archive, string entryName, bool allowMissing = false)
    {
        var entry = archive.GetEntry(entryName);
        if (entry is null && allowMissing) return "";
        if (entry is null) throw new InvalidDataException($"Excel 缺少文件：{entryName}");
        using var reader = new StreamReader(entry.Open(), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static List<string> ParseSharedStrings(string xml)
    {
        if (string.IsNullOrEmpty(xml)) return [];
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        return document.Descendants().Where(element => element.Name.LocalName == "si")
            .Select(item => string.Concat(item.Descendants().Where(element => element.Name.LocalName == "t").Select(element => element.Value)))
            .ToList();
    }

    private static List<WorkbookSheet> ParseWorkbook(string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        return document.Descendants().Where(element => element.Name.LocalName == "sheet")
            .Where(element => !string.Equals(Attribute(element, "state"), "hidden", StringComparison.OrdinalIgnoreCase))
            .Select(element => new WorkbookSheet(
                Attribute(element, "name") ?? throw new InvalidDataException("Sheet 缺少名称"),
                element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "id")?.Value
                    ?? throw new InvalidDataException("Sheet 缺少关系 ID")))
            .ToList();
    }

    private static Dictionary<string, string> ParseRelationships(string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        return document.Descendants().Where(element => element.Name.LocalName == "Relationship")
            .Where(element => (Attribute(element, "Type") ?? "").Contains("/worksheet", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                element => Attribute(element, "Id") ?? throw new InvalidDataException("工作簿关系缺少 ID"),
                element => Attribute(element, "Target") ?? throw new InvalidDataException("工作簿关系缺少目标"));
    }

    private static List<CellRecord> ParseWorksheet(string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var cells = new List<CellRecord>();
        foreach (var cell in document.Descendants().Where(element => element.Name.LocalName == "c"))
        {
            var reference = Attribute(cell, "r");
            if (reference is null || !TryCoordinate(reference, out var row, out var column)) continue;
            var type = Attribute(cell, "t");
            var style = int.TryParse(Attribute(cell, "s"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedStyle)
                ? parsedStyle
                : (int?)null;
            var value = type == "inlineStr"
                ? string.Concat(cell.Descendants().Where(element => element.Name.LocalName == "t").Select(element => element.Value))
                : cell.Elements().FirstOrDefault(element => element.Name.LocalName == "v")?.Value ?? "";
            cells.Add(new CellRecord(row, column, type, style, value));
        }
        return cells;
    }

    private static HashSet<int> ParseDateStyleIndexes(string xml)
    {
        if (string.IsNullOrEmpty(xml)) return [];
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var customFormats = document.Descendants().Where(element => element.Name.LocalName == "numFmt")
            .Where(element => int.TryParse(Attribute(element, "numFmtId"), out _) && LooksLikeDateFormat(Attribute(element, "formatCode") ?? ""))
            .Select(element => int.Parse(Attribute(element, "numFmtId")!, CultureInfo.InvariantCulture))
            .ToHashSet();
        var cellXfs = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "cellXfs");
        if (cellXfs is null) return [];
        var dates = new HashSet<int>();
        var index = 0;
        foreach (var xf in cellXfs.Elements().Where(element => element.Name.LocalName == "xf"))
        {
            var formatId = int.TryParse(Attribute(xf, "numFmtId"), out var parsed) ? parsed : 0;
            if ((formatId is >= 14 and <= 22) || (formatId is >= 45 and <= 47) || customFormats.Contains(formatId)) dates.Add(index);
            index++;
        }
        return dates;
    }

    private static List<List<string>> MakeMatrix(List<CellRecord> cells, List<string> sharedStrings, HashSet<int> dateStyles)
    {
        if (cells.Count == 0) return [];
        var maxRow = cells.Max(cell => cell.Row);
        var maxColumn = cells.Max(cell => cell.Column);
        var matrix = Enumerable.Range(0, maxRow + 1).Select(_ => Enumerable.Repeat("", maxColumn + 1).ToList()).ToList();
        foreach (var cell in cells)
        {
            var display = cell.Value;
            if (cell.Type == "s" && int.TryParse(cell.Value, out var index) && index >= 0 && index < sharedStrings.Count)
                display = sharedStrings[index];
            else if (cell.Type == "b")
                display = cell.Value == "1" ? "true" : "false";
            else if (cell.Style is int style && dateStyles.Contains(style) && double.TryParse(cell.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
                display = FormatExcelDate(serial);
            matrix[cell.Row][cell.Column] = display;
        }
        while (matrix.Count > 0 && matrix[^1].All(string.IsNullOrEmpty)) matrix.RemoveAt(matrix.Count - 1);
        var columnCount = matrix.Count == 0 ? 0 : matrix.Max(row => row.FindLastIndex(value => !string.IsNullOrEmpty(value)) + 1);
        return matrix.Select(row => row.Take(columnCount).ToList()).ToList();
    }

    private static string FormatExcelDate(double serial)
    {
        var date = new DateTime(1899, 12, 30, 0, 0, 0, DateTimeKind.Utc).AddDays(serial);
        var format = Math.Truncate(serial) == serial ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss";
        return date.ToString(format, CultureInfo.GetCultureInfo("zh-CN"));
    }

    private static bool TryCoordinate(string reference, out int row, out int column)
    {
        column = 0;
        var digits = "";
        foreach (var character in reference)
        {
            if (character is >= 'A' and <= 'Z') column = column * 26 + character - 'A' + 1;
            else if (character is >= 'a' and <= 'z') column = column * 26 + character - 'a' + 1;
            else if (character is >= '0' and <= '9') digits += character;
        }
        if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRow) || parsedRow < 1 || column < 1)
        {
            row = 0;
            return false;
        }
        row = parsedRow - 1;
        column--;
        return true;
    }

    private static string? Attribute(XElement element, string localName) => element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == localName)?.Value;

    private static bool LooksLikeDateFormat(string code)
    {
        var stripped = Regex.Replace(code, "\\\"[^\\\"]*\\\"", "", RegexOptions.CultureInvariant).ToLowerInvariant();
        return stripped.Contains("yy", StringComparison.Ordinal) || stripped.Contains("dd", StringComparison.Ordinal) || stripped.Contains("hh", StringComparison.Ordinal) || stripped.Contains("ss", StringComparison.Ordinal);
    }
}
