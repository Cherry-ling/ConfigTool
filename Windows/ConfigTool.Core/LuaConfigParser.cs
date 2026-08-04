using System.Text;
using System.Text.RegularExpressions;

namespace PairPair.ConfigTool.Core;

internal enum LuaBindingKind { String, LongString, Number, Bool, Nil, Table, Raw }

internal sealed record LuaBinding(int Start, int End, LuaBindingKind Kind);

internal sealed record ParsedLuaConfig(WorkbookView View, Dictionary<string, LuaBinding> Bindings);

internal abstract class LuaValue(int start, int end)
{
    public int Start { get; } = start;
    public int End { get; } = end;
    public virtual List<LuaEntry>? TableEntries => null;
    public abstract string? KeyText { get; }
    public abstract LuaBindingKind Kind { get; }
}

internal sealed class LuaScalarValue(string text, int start, int end, LuaBindingKind kind) : LuaValue(start, end)
{
    public string Text { get; } = text;
    public override string? KeyText => Text;
    public override LuaBindingKind Kind { get; } = kind;
}

internal sealed class LuaBoolValue(bool value, int start, int end) : LuaValue(start, end)
{
    public bool Value { get; } = value;
    public override string? KeyText => Value ? "true" : "false";
    public override LuaBindingKind Kind => LuaBindingKind.Bool;
}

internal sealed class LuaNilValue(int start, int end) : LuaValue(start, end)
{
    public override string? KeyText => null;
    public override LuaBindingKind Kind => LuaBindingKind.Nil;
}

internal sealed class LuaTableValue(List<LuaEntry> entries, int start, int end) : LuaValue(start, end)
{
    public override List<LuaEntry> TableEntries { get; } = entries;
    public override string? KeyText => null;
    public override LuaBindingKind Kind => LuaBindingKind.Table;
}

internal sealed record LuaEntry(LuaValue? Key, int ImplicitIndex, LuaValue Value);

internal sealed class LuaParser
{
    private readonly byte[] _bytes;
    private int _index;

    public LuaParser(byte[] bytes) => _bytes = bytes;
    public LuaParser(string text) : this(Encoding.UTF8.GetBytes(text)) { }

    public LuaValue ParseMainTable()
    {
        var start = FindBytes(Encoding.ASCII.GetBytes("local tmp"));
        if (start < 0) throw Error("未找到 local tmp 配置表");
        var brace = Array.IndexOf(_bytes, (byte)'{', start);
        if (brace < 0) throw Error("未找到 local tmp 配置表");
        _index = brace;
        return ParseValue();
    }

    public LuaValue ParseSingleValue()
    {
        _index = 0;
        SkipTrivia();
        var value = ParseValue();
        SkipTrivia();
        if (_index != _bytes.Length) throw Error("Lua 值后存在无法识别的内容");
        return value;
    }

    private int FindBytes(byte[] needle)
    {
        if (needle.Length == 0 || _bytes.Length < needle.Length) return -1;
        for (var offset = 0; offset <= _bytes.Length - needle.Length; offset++)
        {
            var matched = true;
            for (var index = 0; index < needle.Length; index++)
            {
                if (_bytes[offset + index] == needle[index]) continue;
                matched = false;
                break;
            }
            if (matched) return offset;
        }
        return -1;
    }

    private LuaValue ParseValue()
    {
        SkipTrivia();
        if (_index >= _bytes.Length) throw Error("Lua 值意外结束");
        var start = _index;
        return _bytes[_index] switch
        {
            (byte)'{' => ParseTable(),
            (byte)'"' or (byte)'\'' => ParseQuotedString(),
            (byte)'[' when LongStringLevel(_index) is not null => ParseLongString(),
            (byte)'-' or >= (byte)'0' and <= (byte)'9' => ParseNumberOrRaw(),
            _ => ParseIdentifierValue(start)
        };
    }

    private LuaValue ParseIdentifierValue(int start)
    {
        var token = ParseIdentifier();
        return token switch
        {
            "true" => new LuaBoolValue(true, start, _index),
            "false" => new LuaBoolValue(false, start, _index),
            "nil" => new LuaNilValue(start, _index),
            _ => new LuaScalarValue(token, start, _index, LuaBindingKind.Raw)
        };
    }

    private LuaValue ParseTable()
    {
        var start = _index++;
        var entries = new List<LuaEntry>();
        var implicitIndex = 1;
        while (true)
        {
            SkipTrivia();
            if (_index >= _bytes.Length) throw Error("Lua table 缺少 }");
            if (_bytes[_index] == (byte)'}')
            {
                _index++;
                return new LuaTableValue(entries, start, _index);
            }

            LuaValue? key = null;
            LuaValue value;
            if (_bytes[_index] == (byte)'[' && LongStringLevel(_index) is null)
            {
                _index++;
                key = ParseValue();
                SkipTrivia();
                Consume((byte)']', "Lua table key 缺少 ]");
                SkipTrivia();
                Consume((byte)'=', "Lua table key 缺少 =");
                value = ParseValue();
            }
            else
            {
                var saved = _index;
                if (IsIdentifierStart(_bytes[_index]))
                {
                    var identifier = ParseIdentifier();
                    SkipTrivia();
                    if (_index < _bytes.Length && _bytes[_index] == (byte)'=')
                    {
                        _index++;
                        key = new LuaScalarValue(identifier, saved, saved + Encoding.UTF8.GetByteCount(identifier), LuaBindingKind.String);
                        value = ParseValue();
                    }
                    else
                    {
                        _index = saved;
                        value = ParseValue();
                    }
                }
                else
                {
                    value = ParseValue();
                }
            }
            entries.Add(new LuaEntry(key, implicitIndex++, value));
            SkipTrivia();
            if (_index < _bytes.Length && (_bytes[_index] == (byte)',' || _bytes[_index] == (byte)';')) _index++;
        }
    }

    private LuaValue ParseQuotedString()
    {
        var start = _index;
        var quote = _bytes[_index++];
        var output = new List<byte>();
        while (_index < _bytes.Length)
        {
            var current = _bytes[_index++];
            if (current == quote) return new LuaScalarValue(Encoding.UTF8.GetString(output.ToArray()), start, _index, LuaBindingKind.String);
            if (current == (byte)'\\' && _index < _bytes.Length)
            {
                var escaped = _bytes[_index++];
                output.Add(escaped switch
                {
                    (byte)'n' => (byte)'\n',
                    (byte)'r' => (byte)'\r',
                    (byte)'t' => (byte)'\t',
                    (byte)'\\' => (byte)'\\',
                    (byte)'"' => (byte)'"',
                    (byte)'\'' => (byte)'\'',
                    _ => escaped
                });
            }
            else output.Add(current);
        }
        throw Error("Lua 字符串缺少结束引号");
    }

    private int? LongStringLevel(int position)
    {
        if (position >= _bytes.Length || _bytes[position] != (byte)'[') return null;
        var cursor = position + 1;
        while (cursor < _bytes.Length && _bytes[cursor] == (byte)'=') cursor++;
        return cursor < _bytes.Length && _bytes[cursor] == (byte)'[' ? cursor - position - 1 : null;
    }

    private LuaValue ParseLongString()
    {
        var start = _index;
        var level = LongStringLevel(_index) ?? 0;
        _index += level + 2;
        var contentStart = _index;
        var closing = Encoding.ASCII.GetBytes("]" + new string('=', level) + "]");
        while (_index + closing.Length <= _bytes.Length)
        {
            var matched = true;
            for (var offset = 0; offset < closing.Length; offset++)
            {
                if (_bytes[_index + offset] == closing[offset]) continue;
                matched = false;
                break;
            }
            if (matched)
            {
                var text = Encoding.UTF8.GetString(_bytes, contentStart, _index - contentStart);
                _index += closing.Length;
                return new LuaScalarValue(text, start, _index, LuaBindingKind.LongString);
            }
            _index++;
        }
        throw Error("Lua 长字符串缺少结束标记");
    }

    private LuaValue ParseNumberOrRaw()
    {
        var start = _index;
        while (_index < _bytes.Length && !IsDelimiter(_bytes[_index])) _index++;
        var token = Encoding.UTF8.GetString(_bytes, start, _index - start);
        return new LuaScalarValue(token, start, _index, Regex.IsMatch(token, @"^-?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d)?$", RegexOptions.CultureInvariant)
            ? LuaBindingKind.Number
            : LuaBindingKind.Raw);
    }

    private string ParseIdentifier()
    {
        var start = _index;
        while (_index < _bytes.Length && IsIdentifierPart(_bytes[_index])) _index++;
        if (start == _index)
            while (_index < _bytes.Length && !IsDelimiter(_bytes[_index])) _index++;
        return Encoding.UTF8.GetString(_bytes, start, _index - start);
    }

    private void SkipTrivia()
    {
        while (_index < _bytes.Length)
        {
            if (char.IsWhiteSpace((char)_bytes[_index]))
            {
                _index++;
                continue;
            }
            if (_index + 1 < _bytes.Length && _bytes[_index] == (byte)'-' && _bytes[_index + 1] == (byte)'-')
            {
                _index += 2;
                while (_index < _bytes.Length && _bytes[_index] != (byte)'\n') _index++;
                continue;
            }
            break;
        }
    }

    private void Consume(byte expected, string message)
    {
        if (_index >= _bytes.Length || _bytes[_index] != expected) throw Error(message);
        _index++;
    }

    private static bool IsDelimiter(byte value) => value is (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)' ' or (byte)',' or (byte)';' or (byte)'}' or (byte)']';
    private static bool IsIdentifierStart(byte value) => value == (byte)'_' || value is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z';
    private static bool IsIdentifierPart(byte value) => IsIdentifierStart(value) || value is >= (byte)'0' and <= (byte)'9';
    private ConfigSaveException Error(string message) => new($"{message}（字节 {_index}）");
}

internal sealed class LuaConfigParser
{
    public List<WorkbookView> Parse(string path) => [ParseDocument(path).View];

    public ParsedLuaConfig ParseDocument(string path)
    {
        var data = File.ReadAllBytes(path);
        var source = Encoding.UTF8.GetString(data);
        var root = new LuaParser(data).ParseMainTable();
        var rootEntries = root.TableEntries ?? throw new ConfigSaveException("tmp 不是 Lua table");
        var returnedName = ReturnTableName(source);
        var selected = rootEntries.FirstOrDefault(entry => entry.Key?.KeyText == returnedName)
            ?? rootEntries.FirstOrDefault(entry => entry.Key?.KeyText is string name && entry.Value.TableEntries is not null && !name.EndsWith("AB", StringComparison.Ordinal) && !name.EndsWith("Patch", StringComparison.Ordinal));
        if (selected?.Value.TableEntries is not List<LuaEntry> entries) throw new ConfigSaveException("未找到 return 对应的 Lua 配置表");
        var tableName = selected.Key?.KeyText ?? Path.GetFileNameWithoutExtension(path);
        var built = BuildMatrix(entries, data);
        var file = new FileInfo(path);
        return new ParsedLuaConfig(new WorkbookView
        {
            Id = $"{Path.GetFileName(path)}::{tableName}",
            Name = Path.GetFileNameWithoutExtension(path),
            FileName = Path.GetFileName(path),
            SheetName = tableName,
            SheetCount = 1,
            Category = ConfigCategory.NameFor(Path.GetFileNameWithoutExtension(path)),
            ModifiedAt = ConfigFiles.IsoTimestamp(file.LastWriteTimeUtc),
            RowCount = built.Rows.Count,
            ColumnCount = built.Rows.Count == 0 ? 0 : built.Rows.Max(row => row.Count),
            Rows = built.Rows,
            SourceKind = "lua",
            SourceSignature = ConfigFiles.Signature(path),
            EditableFromRow = 3,
            LockedCells = built.LockedCells,
            IsLoaded = true
        }, built.Bindings);
    }

    private static string? ReturnTableName(string source)
    {
        var match = Regex.Match(source, @"return\s+tmp\s*\[\s*[\""']([^\""']+)[\""']\s*\]", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static (List<List<string>> Rows, Dictionary<string, LuaBinding> Bindings, List<string> LockedCells) BuildMatrix(List<LuaEntry> entries, byte[] source)
    {
        var recordEntries = entries.Where(entry => entry.Value.TableEntries is not null).ToList();
        if (recordEntries.Count > 0)
        {
            var columns = new List<string>();
            foreach (var entry in recordEntries)
                foreach (var field in entry.Value.TableEntries!)
                    if (field.Key?.KeyText is string name && !columns.Contains(name, StringComparer.Ordinal)) columns.Add(name);
            var rows = new List<List<string>>
            {
                new(new[] { "键" }.Concat(columns)),
                new(new[] { "key" }.Concat(columns)),
                new(new[] { "Lua" }.Concat(columns.Select(_ => "")))
            };
            var bindings = new Dictionary<string, LuaBinding>();
            var locked = new List<string>();
            foreach (var entry in entries)
            {
                var rowIndex = rows.Count;
                var row = new List<string> { entry.Key?.KeyText ?? entry.ImplicitIndex.ToString() };
                if (entry.Key is null) locked.Add($"{rowIndex}:0"); else bindings[$"{rowIndex}:0"] = BindingFor(entry.Key);
                var fields = entry.Value.TableEntries ?? [];
                for (var columnOffset = 0; columnOffset < columns.Count; columnOffset++)
                {
                    var field = fields.FirstOrDefault(candidate => candidate.Key?.KeyText == columns[columnOffset]);
                    if (field is null)
                    {
                        row.Add("");
                        locked.Add($"{rowIndex}:{columnOffset + 1}");
                    }
                    else
                    {
                        row.Add(Display(field.Value, source));
                        bindings[$"{rowIndex}:{columnOffset + 1}"] = BindingFor(field.Value);
                    }
                }
                rows.Add(row);
            }
            return (rows, bindings, locked);
        }

        var scalarRows = new List<List<string>>
        {
            new() { "键", "值" },
            new() { "key", "value" },
            new() { "Lua", "" }
        };
        var scalarBindings = new Dictionary<string, LuaBinding>();
        var scalarLocked = new List<string>();
        foreach (var entry in entries)
        {
            var rowIndex = scalarRows.Count;
            scalarRows.Add([entry.Key?.KeyText ?? entry.ImplicitIndex.ToString(), Display(entry.Value, source)]);
            if (entry.Key is null) scalarLocked.Add($"{rowIndex}:0"); else scalarBindings[$"{rowIndex}:0"] = BindingFor(entry.Key);
            scalarBindings[$"{rowIndex}:1"] = BindingFor(entry.Value);
        }
        return (scalarRows, scalarBindings, scalarLocked);
    }

    private static string Display(LuaValue value, byte[] source) => value switch
    {
        LuaScalarValue scalar => scalar.Text,
        LuaBoolValue boolean => boolean.Value ? "true" : "false",
        LuaNilValue => "nil",
        LuaTableValue table => Encoding.UTF8.GetString(source, table.Start, table.End - table.Start),
        _ => ""
    };

    private static LuaBinding BindingFor(LuaValue value) => new(value.Start, value.End, value.Kind);
}
