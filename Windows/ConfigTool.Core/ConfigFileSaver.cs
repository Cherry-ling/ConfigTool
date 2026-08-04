using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PairPair.ConfigTool.Core;

public sealed class ConfigFileSaver
{
    public void Save(string directory, string id, string expectedSignature, List<CellChange> changes)
    {
        var separator = id.IndexOf("::", StringComparison.Ordinal);
        if (separator <= 0) throw new ConfigSaveException("配置标识无效");
        var fileName = id[..separator];
        var sheetName = id[(separator + 2)..];
        var path = Path.Combine(directory, fileName);
        if (!string.Equals(ConfigFiles.Signature(path), expectedSignature, StringComparison.Ordinal)) throw ConfigSaveException.SourceChangedException();
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".lua":
                SaveLua(path, expectedSignature, changes);
                break;
            case ".xlsx":
                SaveXlsx(path, sheetName, expectedSignature, changes);
                break;
            default:
                throw new ConfigSaveException($"暂不支持保存 {Path.GetExtension(path)} 文件");
        }
    }

    private static void SaveLua(string path, string expectedSignature, List<CellChange> changes)
    {
        var parsed = new LuaConfigParser().ParseDocument(path);
        var replacements = new List<(LuaBinding Binding, byte[] Value)>();
        foreach (var change in changes)
        {
            if (!parsed.Bindings.TryGetValue($"{change.Row}:{change.Column}", out var binding))
                throw new ConfigSaveException("该 Lua 单元格由结构生成，暂不能新增字段。");
            replacements.Add((binding, EncodeLua(change.Value, binding.Kind)));
        }
        var bytes = File.ReadAllBytes(path).ToList();
        foreach (var replacement in replacements.OrderByDescending(item => item.Binding.Start))
        {
            bytes.RemoveRange(replacement.Binding.Start, replacement.Binding.End - replacement.Binding.Start);
            bytes.InsertRange(replacement.Binding.Start, replacement.Value);
        }
        var result = bytes.ToArray();
        _ = new LuaParser(result).ParseMainTable();
        WriteAtomically(path, result, expectedSignature);
    }

    private static byte[] EncodeLua(string value, LuaBindingKind kind)
    {
        return kind switch
        {
            LuaBindingKind.String => Encoding.UTF8.GetBytes($"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal)}\""),
            LuaBindingKind.LongString => Encoding.UTF8.GetBytes(EncodeLongString(value)),
            LuaBindingKind.Number when Regex.IsMatch(value, @"^-?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d)?$", RegexOptions.CultureInvariant) => Encoding.UTF8.GetBytes(value),
            LuaBindingKind.Number => throw new ConfigSaveException("数字字段只能保存合法数字。"),
            LuaBindingKind.Bool when value is "true" or "false" => Encoding.UTF8.GetBytes(value),
            LuaBindingKind.Bool => throw new ConfigSaveException("布尔字段只能填写 true 或 false。"),
            LuaBindingKind.Nil when value == "nil" => Encoding.UTF8.GetBytes(value),
            LuaBindingKind.Nil => throw new ConfigSaveException("nil 字段只能保持 nil。"),
            LuaBindingKind.Table => ValidateLuaTable(value),
            LuaBindingKind.Raw when !string.IsNullOrWhiteSpace(value) => Encoding.UTF8.GetBytes(value),
            LuaBindingKind.Raw => throw new ConfigSaveException("Lua 值不能为空。"),
            _ => throw new ConfigSaveException("Lua 值格式无效。")
        };
    }

    private static string EncodeLongString(string value)
    {
        var level = 0;
        while (value.Contains("]" + new string('=', level) + "]", StringComparison.Ordinal)) level++;
        var equals = new string('=', level);
        return $"[{equals}[{value}]{equals}]";
    }

    private static byte[] ValidateLuaTable(string value)
    {
        _ = new LuaParser(value).ParseSingleValue();
        return Encoding.UTF8.GetBytes(value);
    }

    private static void SaveXlsx(string path, string sheetName, string expectedSignature, List<CellChange> changes)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new ConfigSaveException("配置目录无效");
        var temporary = Path.Combine(directory, $".ConfigTool-{Guid.NewGuid():N}.xlsx");
        try
        {
            File.Copy(path, temporary, overwrite: false);
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Update))
            {
                var workbook = ReadXml(archive, "xl/workbook.xml");
                var sheet = workbook.Descendants().FirstOrDefault(element => element.Name.LocalName == "sheet" && Attribute(element, "name") == sheetName)
                    ?? throw new ConfigSaveException($"找不到 Excel Sheet：{sheetName}");
                var relationshipId = sheet.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "id")?.Value;
                var relationships = ReadXml(archive, "xl/_rels/workbook.xml.rels");
                var target = relationships.Descendants().FirstOrDefault(element => element.Name.LocalName == "Relationship" && Attribute(element, "Id") == relationshipId)?.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "Target")?.Value
                    ?? throw new ConfigSaveException("无法定位 Excel Sheet 文件");
                var worksheetPath = NormalizeTarget(target);
                var worksheet = ReadXml(archive, worksheetPath);
                var sheetData = worksheet.Descendants().FirstOrDefault(element => element.Name.LocalName == "sheetData")
                    ?? throw new ConfigSaveException("Excel Sheet 缺少 sheetData");
                var cells = worksheet.Descendants().Where(element => element.Name.LocalName == "c")
                    .Where(element => Attribute(element, "r") is not null)
                    .ToDictionary(element => Attribute(element, "r")!, StringComparer.OrdinalIgnoreCase);
                foreach (var change in changes)
                {
                    var reference = ColumnLetters(change.Column + 1) + (change.Row + 1);
                    if (!cells.TryGetValue(reference, out var cell))
                    {
                        cell = MakeCell(reference, change.Row + 1, sheetData);
                        cells[reference] = cell;
                    }
                    SetCell(cell, change.Value);
                }
                WriteXml(archive, worksheetPath, worksheet);
            }
            using (var validation = ZipFile.OpenRead(temporary))
            {
                if (validation.GetEntry("xl/workbook.xml") is null) throw new ConfigSaveException("写入后的 Excel 无效");
            }
            if (!string.Equals(ConfigFiles.Signature(path), expectedSignature, StringComparison.Ordinal)) throw ConfigSaveException.SourceChangedException();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string NormalizeTarget(string target) => target.StartsWith('/') ? target[1..] : target.StartsWith("xl/", StringComparison.Ordinal) ? target : "xl/" + target.Replace("../", "", StringComparison.Ordinal);

    private static XDocument ReadXml(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new ConfigSaveException($"Excel 缺少文件：{name}");
        using var stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    private static void WriteXml(ZipArchive archive, string name, XDocument document)
    {
        archive.GetEntry(name)?.Delete();
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        document.Save(stream, SaveOptions.DisableFormatting);
    }

    private static XElement MakeCell(string reference, int rowNumber, XElement sheetData)
    {
        var row = sheetData.Elements().FirstOrDefault(element => element.Name.LocalName == "row" && Attribute(element, "r") == rowNumber.ToString());
        if (row is null)
        {
            row = new XElement(sheetData.Name.Namespace + "row", new XAttribute("r", rowNumber));
            var firstAfter = sheetData.Elements().FirstOrDefault(element => element.Name.LocalName == "row" && int.TryParse(Attribute(element, "r"), out var number) && number > rowNumber);
            if (firstAfter is null) sheetData.Add(row); else firstAfter.AddBeforeSelf(row);
        }
        var cell = new XElement(row.Name.Namespace + "c", new XAttribute("r", reference));
        var newColumn = ColumnNumber(reference);
        var firstAfterCell = row.Elements().FirstOrDefault(element => element.Name.LocalName == "c" && ColumnNumber(Attribute(element, "r") ?? "") > newColumn);
        if (firstAfterCell is null) row.Add(cell); else firstAfterCell.AddBeforeSelf(cell);
        return cell;
    }

    private static void SetCell(XElement cell, string value)
    {
        cell.RemoveNodes();
        cell.Attributes().Where(attribute => attribute.Name.LocalName == "t").Remove();
        var ns = cell.Name.Namespace;
        if (value.StartsWith("=", StringComparison.Ordinal))
        {
            cell.Add(new XElement(ns + "f", value[1..]));
        }
        else if (Regex.IsMatch(value, @"^-?(?:0|[1-9]\d*)(?:\.\d+)?$", RegexOptions.CultureInvariant))
        {
            cell.Add(new XElement(ns + "v", value));
        }
        else
        {
            cell.SetAttributeValue("t", "inlineStr");
            var text = new XElement(ns + "t", value);
            if (value.StartsWith(' ') || value.EndsWith(' ')) text.SetAttributeValue(XNamespace.Xml + "space", "preserve");
            cell.Add(new XElement(ns + "is", text));
        }
    }

    private static int ColumnNumber(string reference)
    {
        var column = 0;
        foreach (var character in reference)
        {
            if (character is >= 'A' and <= 'Z') column = column * 26 + character - 'A' + 1;
            else if (character is >= 'a' and <= 'z') column = column * 26 + character - 'a' + 1;
        }
        return column;
    }

    private static string ColumnLetters(int column)
    {
        var output = "";
        while (column > 0)
        {
            column--;
            output = (char)('A' + column % 26) + output;
            column /= 26;
        }
        return output;
    }

    private static string? Attribute(XElement element, string localName) => element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == localName)?.Value;

    private static void WriteAtomically(string path, byte[] data, string expectedSignature)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".ConfigTool-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, data);
            if (!string.Equals(ConfigFiles.Signature(path), expectedSignature, StringComparison.Ordinal)) throw ConfigSaveException.SourceChangedException();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
