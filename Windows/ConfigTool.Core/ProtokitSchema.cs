using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PairPair.ConfigTool.Core;

// Reads the FileDescriptorProto embedded by protoc in the selected project's
// generated C#. No dependency on Unity, a protobuf compiler, or a fixed schema.
public sealed class ProtokitSchema
{
    private sealed record Field(string Name, int Number, int Type, bool Repeated, string TypeName);
    private sealed record Message(List<Field> Fields, bool MapEntry);
    private readonly Dictionary<string, Message> _messages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, int>> _enums = new(StringComparer.Ordinal);
    public List<byte[]> Descriptors { get; } = [];

    public static ProtokitSchema Load(string jsonPath)
    {
        var folder = FindDirectory(jsonPath);
        var schema = new ProtokitSchema();
        foreach (var file in Directory.EnumerateFiles(folder, "*.cs").Order(StringComparer.Ordinal))
        {
            var text = File.ReadAllText(file);
            var match = Regex.Match(text, @"byte\[\] descriptorData\s*=.*?string\.Concat\((.*?)\)\);", RegexOptions.Singleline);
            if (!match.Success) continue;
            var encoded = string.Concat(Regex.Matches(match.Groups[1].Value, "\"([A-Za-z0-9+/=]+)\"").Select(part => part.Groups[1].Value));
            var data = Convert.FromBase64String(encoded);
            schema.Descriptors.Add(data);
            var fields = Wire.Read(data);
            var package = "." + Wire.Text(fields, 2);
            foreach (var message in fields.Where(field => field.Number == 4)) schema.AddMessage(package, message.Data);
            foreach (var item in fields.Where(field => field.Number == 5)) schema.AddEnum(package, item.Data);
        }
        return schema;
    }

    private static string FindDirectory(string jsonPath)
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(jsonPath)!); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "Scripts", "Game", "GameHotfix", "GameProto", "Runtime", "gen", "rawdata");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new ConfigSaveException("找不到当前项目的 Protobuf 字段定义（Assets/Scripts/Game/GameHotfix/GameProto/Runtime/gen/rawdata）。为保证 JSON 与 .bytes 一致，未保存任何文件。");
    }

    private void AddMessage(string parent, byte[] data)
    {
        var fields = Wire.Read(data);
        var name = parent + "." + Wire.Text(fields, 1);
        var definitions = fields.Where(field => field.Number == 2).Select(field =>
        {
            var parts = Wire.Read(field.Data);
            return new Field(Wire.Text(parts, 1), (int)Wire.Number(parts, 3), (int)Wire.Number(parts, 5), Wire.Number(parts, 4) == 3, Wire.Text(parts, 6));
        }).OrderBy(field => field.Number).ToList();
        var options = fields.FirstOrDefault(field => field.Number == 7);
        var mapEntry = options is not null && Wire.Number(Wire.Read(options.Data), 7) == 1;
        _messages[name] = new Message(definitions, mapEntry);
        foreach (var nested in fields.Where(field => field.Number == 3)) AddMessage(name, nested.Data);
        foreach (var item in fields.Where(field => field.Number == 4)) AddEnum(name, item.Data);
    }

    private void AddEnum(string parent, byte[] data)
    {
        var fields = Wire.Read(data);
        _enums[parent + "." + Wire.Text(fields, 1)] = fields.Where(field => field.Number == 2).Select(field => Wire.Read(field.Data))
            .ToDictionary(parts => Wire.Text(parts, 1), parts => unchecked((int)Wire.Number(parts, 2)), StringComparer.Ordinal);
    }

    public byte[] Encode(string table, JsonObject root) => EncodeMessage(".rawdata." + table, root);

    private byte[] EncodeMessage(string name, JsonObject value)
    {
        if (!_messages.TryGetValue(name, out var message)) throw new ConfigSaveException($"缺少 Protobuf 类型定义：{name}");
        foreach (var key in value.Select(property => property.Key))
            if (!message.Fields.Any(field => field.Name == key)) throw new ConfigSaveException($"{name} 不存在字段 {key}，请先更新项目生成代码。");
        using var output = new MemoryStream();
        foreach (var field in message.Fields)
        {
            var cell = value[field.Name];
            if (cell is null) continue;
            if (field.Type == 11 && _messages.TryGetValue(field.TypeName, out var target) && target.MapEntry)
            {
                if (cell is not JsonObject map) throw new ConfigSaveException($"{field.Name} 必须是 Map 对象。");
                var keyField = target.Fields.Single(item => item.Number == 1);
                foreach (var entry in map)
                {
                    JsonNode key = keyField.Type == 9 ? JsonValue.Create(entry.Key)! : JsonNode.Parse(entry.Key) ?? throw new ConfigSaveException("Map 键无效");
                    var item = new JsonObject { ["key"] = key, ["value"] = entry.Value?.DeepClone() };
                    WriteField(output, field.Number, 2, EncodeMessage(field.TypeName, item));
                }
            }
            else if (field.Repeated)
            {
                if (cell is not JsonArray array) throw new ConfigSaveException($"{field.Name} 必须是数组。");
                if (field.Type is not (9 or 11 or 12))
                {
                    using var packed = new MemoryStream();
                    foreach (var item in array) packed.Write(Scalar(field, item ?? throw new ConfigSaveException($"{field.Name} 数组不能包含 null")).Data);
                    if (packed.Length > 0) WriteField(output, field.Number, 2, packed.ToArray());
                }
                else foreach (var item in array)
                {
                    var encoded = Scalar(field, item ?? throw new ConfigSaveException($"{field.Name} 数组不能包含 null"));
                    WriteField(output, field.Number, encoded.WireType, encoded.Data);
                }
            }
            else
            {
                var encoded = Scalar(field, cell);
                if (!encoded.Default) WriteField(output, field.Number, encoded.WireType, encoded.Data);
            }
        }
        return output.ToArray();
    }

    private (int WireType, byte[] Data, bool Default) Scalar(Field field, JsonNode value)
    {
        try
        {
            if (field.Type == 11) return (2, EncodeMessage(field.TypeName, value.AsObject()), false);
            if (field.Type is 9 or 12)
            {
                var text = value.GetValue<string>();
                return (2, field.Type == 9 ? Encoding.UTF8.GetBytes(text) : Convert.FromBase64String(text), text.Length == 0);
            }
            var raw = value.ToJsonString();
            if (field.Type is 1 or 2)
            {
                var number = double.Parse(raw, CultureInfo.InvariantCulture);
                if (!double.IsFinite(number) || (field.Type == 2 && !float.IsFinite((float)number))) throw new FormatException();
                var bytes = new byte[field.Type == 1 ? 8 : 4];
                if (field.Type == 1) BinaryPrimitives.WriteDoubleLittleEndian(bytes, number);
                else BinaryPrimitives.WriteSingleLittleEndian(bytes, (float)number);
                return (field.Type == 1 ? 1 : 5, bytes, number == 0);
            }
            ulong integer = field.Type switch
            {
                8 => value.GetValue<bool>() ? 1UL : 0UL,
                5 => unchecked((ulong)(long)int.Parse(raw, CultureInfo.InvariantCulture)),
                3 or 16 or 18 => unchecked((ulong)long.Parse(raw, CultureInfo.InvariantCulture)),
                4 or 6 => ulong.Parse(raw, CultureInfo.InvariantCulture),
                7 or 13 => uint.Parse(raw, CultureInfo.InvariantCulture),
                15 or 17 => unchecked((ulong)(long)int.Parse(raw, CultureInfo.InvariantCulture)),
                14 => unchecked((ulong)(long)(raw.StartsWith('"') ? _enums[field.TypeName][value.GetValue<string>()] : int.Parse(raw, CultureInfo.InvariantCulture))),
                _ => throw new ConfigSaveException($"暂不支持 Protobuf 字段类型 {field.Type}")
            };
            if (field.Type is 17 or 18) integer = unchecked((integer << 1) ^ (ulong)((long)integer >> 63));
            if (field.Type is 6 or 7 or 15 or 16)
            {
                var bytes = new byte[field.Type is 6 or 16 ? 8 : 4];
                if (bytes.Length == 8) BinaryPrimitives.WriteUInt64LittleEndian(bytes, integer);
                else BinaryPrimitives.WriteUInt32LittleEndian(bytes, unchecked((uint)integer));
                return (bytes.Length == 8 ? 1 : 5, bytes, integer == 0);
            }
            return (0, Wire.Varint(integer), integer == 0);
        }
        catch (Exception error) when (error is FormatException or OverflowException or InvalidOperationException or KeyNotFoundException)
        {
            throw new ConfigSaveException($"{field.Name} 的值与项目 Protobuf 类型不符：{value.ToJsonString()}");
        }
    }

    private static void WriteField(Stream stream, int number, int type, byte[] data)
    {
        stream.Write(Wire.Varint((ulong)((number << 3) | type)));
        if (type == 2) stream.Write(Wire.Varint((ulong)data.Length));
        stream.Write(data);
    }

    private static class Wire
    {
        internal sealed record Part(int Number, ulong Integer, byte[] Data);
        internal static List<Part> Read(byte[] data)
        {
            var parts = new List<Part>();
            var offset = 0;
            while (offset < data.Length)
            {
                var tag = ReadVarint(data, ref offset);
                var type = (int)(tag & 7);
                ulong integer = 0;
                byte[] bytes = [];
                if (type == 0) integer = ReadVarint(data, ref offset);
                else
                {
                    var count = type switch { 1 => 8, 5 => 4, 2 => checked((int)ReadVarint(data, ref offset)), _ => throw new InvalidDataException("无效 Protobuf 描述数据") };
                    if (count > data.Length - offset) throw new InvalidDataException("Protobuf 描述数据不完整");
                    bytes = data.AsSpan(offset, count).ToArray(); offset += count;
                }
                parts.Add(new Part((int)(tag >> 3), integer, bytes));
            }
            return parts;
        }
        internal static string Text(List<Part> fields, int number) => Encoding.UTF8.GetString(fields.FirstOrDefault(field => field.Number == number)?.Data ?? []);
        internal static ulong Number(List<Part> fields, int number) => fields.FirstOrDefault(field => field.Number == number)?.Integer ?? 0;
        internal static byte[] Varint(ulong value)
        {
            var bytes = new List<byte>();
            while (value >= 128) { bytes.Add((byte)((value & 127) | 128)); value >>= 7; }
            bytes.Add((byte)value); return bytes.ToArray();
        }
        private static ulong ReadVarint(byte[] bytes, ref int offset)
        {
            ulong value = 0;
            for (var shift = 0; shift < 70 && offset < bytes.Length; shift += 7)
            {
                var current = bytes[offset++]; value |= (ulong)(current & 127) << shift;
                if (current < 128) return value;
            }
            throw new InvalidDataException("无效 Protobuf varint");
        }
    }
}
