using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PairPair.ConfigTool.Core;

public static class JsonConfigEditor
{
    public static void Save(string path, string sheetName, List<CellChange> changes, string? expectedSignature = null, byte[]? expectedContents = null, byte[]? expectedBinaryContents = null, bool checkBinaryContents = false)
    {
        var original = File.ReadAllBytes(path);
        var binaryPath = Path.ChangeExtension(path, ".bytes");
        var oldBinary = File.Exists(binaryPath) ? File.ReadAllBytes(binaryPath) : null;
        if ((expectedContents is not null && !original.SequenceEqual(expectedContents)) ||
            (checkBinaryContents && !SameBinary(binaryPath, expectedBinaryContents)) ||
            (expectedSignature is not null && ConfigFiles.Signature(path) != expectedSignature))
            throw ConfigSaveException.SourceChangedException();
        var root = JsonNode.Parse(original) ?? throw new ConfigSaveException("JSON 配置为空");
        ApplyChanges(root, sheetName, changes);
        if (root is not JsonObject wrapper) throw new ConfigSaveException("同步 .bytes 的配置顶层必须是项目生成的 Conf 对象。");
        var hashKey = wrapper.Select(property => property.Key).SingleOrDefault(key => key.Equals("md5", StringComparison.OrdinalIgnoreCase))
            ?? throw new ConfigSaveException("配置缺少 MD5 字段，无法确认项目导表格式。");
        var schema = ProtokitSchema.Load(path);
        var table = Path.GetFileNameWithoutExtension(path);
        wrapper[hashKey] = "";
        var payload = schema.Encode(table, wrapper);
        wrapper[hashKey] = Convert.ToHexStringLower(MD5.HashData(payload));
        var binary = schema.Encode(table, wrapper);
        var json = Encoding.UTF8.GetBytes(wrapper.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }) + "\n");
        // Finish all validation before replacing either source. Recheck both
        // files, including .bytes changes made while the encoder was running.
        if (!File.ReadAllBytes(path).SequenceEqual(original) || !SameBinary(binaryPath, oldBinary) ||
            (expectedSignature is not null && ConfigFiles.Signature(path) != expectedSignature))
            throw ConfigSaveException.SourceChangedException();
        WritePair(path, json, binaryPath, binary, original, oldBinary);
    }

    private static void ApplyChanges(JsonNode root, string sheetName, List<CellChange> changes)
    {
        var table = root is JsonObject wrapper && wrapper[sheetName] is JsonObject or JsonArray ? wrapper[sheetName]! : root;
        var map = table as JsonObject;
        var isMap = map is not null && map.All(entry => entry.Value is JsonObject);
        var records = table is JsonArray array ? array.ToList()
            : isMap ? map!.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => entry.Value).ToList()
            : new List<JsonNode?> { table };
        var keys = isMap ? map!.Keys().Order(StringComparer.Ordinal).ToList() : [];
        var fields = records.SelectMany(record => record is JsonObject row ? row.Select(property => property.Key) : ["value"])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(field => field.Equals("id", StringComparison.OrdinalIgnoreCase) || field.Equals("key", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(field => field, StringComparer.Ordinal).ToList();
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            var index = change.Row - 3;
            var column = change.Column - 1;
            if (index < 0 || index >= records.Count || column < 0 || column >= fields.Count)
                throw new ConfigSaveException("JSON 表头、索引列和不存在的单元格不能编辑。");
            var field = fields[column];
            var record = records[index];
            if (record is not JsonObject row || !row.ContainsKey(field))
                throw new ConfigSaveException("当前 JSON 单元格没有对应字段，不能新增字段。");
            var old = row[field];
            var updated = ParseValue(change.Value, old);
            if (isMap && (field.Equals("id", StringComparison.OrdinalIgnoreCase) || field == "key") && Text(old) == keys[index])
                renames[keys[index]] = Text(updated);
            row[field] = updated;
        }
        if (isMap && renames.Count > 0)
        {
            var targetKeys = keys.Select(key => renames.GetValueOrDefault(key, key)).ToList();
            if (targetKeys.Any(string.IsNullOrEmpty) || targetKeys.Distinct(StringComparer.Ordinal).Count() != targetKeys.Count)
                throw new ConfigSaveException("修改后的 Map 主键为空或重复，未保存。");
            var entries = map!.ToList();
            map!.Clear();
            foreach (var entry in entries) map.Add(renames.GetValueOrDefault(entry.Key, entry.Key), entry.Value);
        }
    }

    private static JsonNode? ParseValue(string text, JsonNode? old)
    {
        var kind = old?.GetValueKind() ?? JsonValueKind.Null;
        if (kind == JsonValueKind.String) return JsonValue.Create(text);
        JsonNode? value;
        try { value = JsonNode.Parse(text); }
        catch (JsonException) { throw new ConfigSaveException("JSON 值格式无效：数字、布尔值、数组和对象须使用合法 JSON。"); }
        var next = value?.GetValueKind() ?? JsonValueKind.Null;
        if (kind != JsonValueKind.Null && (kind is JsonValueKind.True or JsonValueKind.False
                ? next is not (JsonValueKind.True or JsonValueKind.False) : next != kind))
            throw new ConfigSaveException("修改后的值类型与原字段不一致。");
        return value;
    }

    private static string Text(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node?.ToJsonString() ?? "";
    private static bool SameBinary(string path, byte[]? original) => original is null ? !File.Exists(path) : File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(original);

    private static void WritePair(string path, byte[] json, string binaryPath, byte[] binary, byte[] original, byte[]? oldBinary)
    {
        var staging = Path.Combine(Path.GetDirectoryName(path)!, ".ConfigTool-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var keepRecovery = false;
        try
        {
            File.WriteAllBytes(Path.Combine(staging, "original.json"), original);
            if (oldBinary is not null) File.WriteAllBytes(Path.Combine(staging, "original.bytes"), oldBinary);
            File.WriteAllBytes(Path.Combine(staging, "new.json"), json);
            File.WriteAllBytes(Path.Combine(staging, "new.bytes"), binary);
            if (!File.ReadAllBytes(path).SequenceEqual(original) || !SameBinary(binaryPath, oldBinary)) throw ConfigSaveException.SourceChangedException();
            File.Move(Path.Combine(staging, "new.bytes"), binaryPath, overwrite: true);
            try { File.Move(Path.Combine(staging, "new.json"), path, overwrite: true); }
            catch
            {
                try
                {
                    if (!File.ReadAllBytes(binaryPath).SequenceEqual(binary)) throw new IOException("二进制被其他程序再次修改");
                    if (oldBinary is null) File.Delete(binaryPath);
                    else File.Move(Path.Combine(staging, "original.bytes"), binaryPath, overwrite: true);
                }
                catch (Exception rollback)
                {
                    keepRecovery = true;
                    throw new ConfigSaveException($"保存失败，自动恢复 .bytes 失败：{rollback.Message}。原始备份位于 {staging}");
                }
                throw;
            }
        }
        finally
        {
            if (!keepRecovery) { try { Directory.Delete(staging, recursive: true); } catch (IOException) { } }
        }
    }

    private static IEnumerable<string> Keys(this JsonObject value) => value.Select(property => property.Key);
}
