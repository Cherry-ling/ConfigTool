using System.Text.Json;

namespace PairPair.ConfigTool.Core;

// Three header rows match the existing spreadsheet and Lua view contract.
public sealed class JsonConfigParser
{
    public List<WorkbookView> Parse(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var name = Path.GetFileNameWithoutExtension(path);
        var tables = new List<(string Name, JsonElement Value)>();
        if (root.ValueKind == JsonValueKind.Object)
        {
            tables.AddRange(root.EnumerateObject()
                .Where(property => (property.Name.EndsWith("Map", StringComparison.Ordinal) || property.Name.EndsWith("Slice", StringComparison.Ordinal)) &&
                    property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => (property.Name, property.Value)));
        }
        else if (root.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("JSON 配置顶层必须是对象或数组");
        if (tables.Count == 0) tables.Add((name, root));
        return tables.Select(table =>
        {
            var rows = BuildMatrix(table.Value);
            return new WorkbookView
            {
                Id = $"{Path.GetFileName(path)}::{table.Name}", Name = name,
                FileName = Path.GetFileName(path), SheetName = table.Name,
                SheetCount = tables.Count, Category = ConfigCategory.NameFor(name),
                ModifiedAt = ConfigFiles.IsoTimestamp(File.GetLastWriteTimeUtc(path)),
                RowCount = rows.Count, ColumnCount = rows[1].Count, Rows = rows,
                SourceKind = "json", SourceSignature = ConfigFiles.Signature(path),
                EditableFromRow = 3,
                LockedCells = rows.SelectMany((row, index) => index < 3 ? Enumerable.Empty<string>() : row.Select((_, column) => $"{index}:{column}").Where((_, column) => column == 0 || !HasField(table.Value, rows[index][0], rows[1][column]))).ToList(),
                IsLoaded = true
            };
        }).ToList();
    }

    private static bool HasField(JsonElement table, string key, string field)
    {
        var record = table.ValueKind == JsonValueKind.Array ? table[int.Parse(key, System.Globalization.CultureInfo.InvariantCulture) - 1]
            : table.ValueKind == JsonValueKind.Object && table.EnumerateObject().All(property => property.Value.ValueKind == JsonValueKind.Object) ? table.GetProperty(key)
            : table;
        return record.ValueKind == JsonValueKind.Object && record.TryGetProperty(field, out _);
    }

    private static List<List<string>> BuildMatrix(JsonElement value)
    {
        var entries = new List<(string Key, JsonElement Value)>();
        var keyHeader = "__key";
        if (value.ValueKind == JsonValueKind.Array)
        {
            entries.AddRange(value.EnumerateArray().Select((item, index) => ((index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), item)));
            keyHeader = "__index";
        }
        else if (value.ValueKind == JsonValueKind.Object && value.EnumerateObject().All(property => property.Value.ValueKind == JsonValueKind.Object))
            entries.AddRange(value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).Select(property => (property.Name, property.Value)));
        else entries.Add(("1", value));

        var records = entries.Select(entry => entry.Value.ValueKind == JsonValueKind.Object
            ? entry.Value.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal)
            : new Dictionary<string, JsonElement> { ["value"] = entry.Value }).ToList();
        var fields = records.SelectMany(record => record.Keys).Distinct(StringComparer.Ordinal)
            .OrderBy(field => field.Equals("id", StringComparison.OrdinalIgnoreCase) || field.Equals("key", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(field => field, StringComparer.Ordinal).ToList();
        while (fields.Contains(keyHeader)) keyHeader = "_" + keyHeader;
        var types = fields.Select(field => records.Where(record => record.ContainsKey(field))
            .Select(record => record[field]).Where(cell => cell.ValueKind != JsonValueKind.Null)
            .Select(TypeName).FirstOrDefault() ?? "").ToList();
        var rows = new List<List<string>>
        {
            new[] { "键 / 序号" }.Concat(fields.Select(_ => "")).ToList(),
            new[] { keyHeader }.Concat(fields).ToList(),
            new[] { "string" }.Concat(types).ToList()
        };
        for (var index = 0; index < entries.Count; index++)
            rows.Add(new[] { entries[index].Key }.Concat(fields.Select(field => records[index].TryGetValue(field, out var cell) ? Display(cell) : "")).ToList());
        return rows;
    }

    private static string TypeName(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "bool",
        _ => "json"
    };

    private static string Display(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? ""
        : System.Text.Json.Nodes.JsonNode.Parse(value.GetRawText())?.ToJsonString() ?? "null";
}
