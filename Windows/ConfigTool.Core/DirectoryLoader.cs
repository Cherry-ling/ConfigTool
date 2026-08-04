using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PairPair.ConfigTool.Core;

public sealed class DirectoryLoader
{
    private sealed record CachedWorkbook(string Signature, List<WorkbookView> Workbooks);

    private readonly WorkbookParser _workbookParser = new();
    private readonly LuaConfigParser _luaParser = new();
    private readonly Dictionary<string, CachedWorkbook> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public string SignatureForDirectory(string directory)
    {
        try
        {
            return string.Join("\n", ConfigUrls(directory).Select(path => $"{Path.GetFileName(path)}|{ConfigFiles.Signature(path)}"));
        }
        catch
        {
            return "";
        }
    }

    public DirectoryPayload Load(string directory, bool includeRows = false)
    {
        var urls = ConfigUrls(directory);
        var workbooks = new List<WorkbookView>();
        lock (_sync)
        {
            foreach (var path in urls)
            {
                var signature = ConfigFiles.Signature(path);
                if (_cache.TryGetValue(path, out var cached) && cached.Signature == signature)
                {
                    workbooks.AddRange(includeRows ? cached.Workbooks : cached.Workbooks.Select(Summary));
                    continue;
                }
                try
                {
                    var parsed = string.Equals(Path.GetExtension(path), ".lua", StringComparison.OrdinalIgnoreCase)
                        ? _luaParser.Parse(path)
                        : _workbookParser.Parse(path);
                    _cache[path] = new CachedWorkbook(signature, parsed);
                    workbooks.AddRange(includeRows ? parsed : parsed.Select(Summary));
                }
                catch (Exception error)
                {
                    var file = new FileInfo(path);
                    workbooks.Add(new WorkbookView
                    {
                        Id = $"{Path.GetFileName(path)}::error",
                        Name = Path.GetFileNameWithoutExtension(path),
                        FileName = Path.GetFileName(path),
                        SheetName = "",
                        SheetCount = 0,
                        Category = ConfigCategory.NameFor(Path.GetFileNameWithoutExtension(path)),
                        ModifiedAt = ConfigFiles.IsoTimestamp(file.LastWriteTimeUtc),
                        RowCount = 0,
                        ColumnCount = 0,
                        Rows = [],
                        SourceKind = Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
                        SourceSignature = ConfigFiles.Signature(path),
                        EditableFromRow = 0,
                        LockedCells = [],
                        IsLoaded = true,
                        Error = error.Message
                    });
                }
            }
        }
        return new DirectoryPayload
        {
            Directory = directory,
            ScannedAt = ConfigFiles.IsoTimestamp(DateTime.UtcNow),
            FileCount = urls.Count,
            Workbooks = workbooks
        };
    }

    public WorkbookView LoadWorkbook(string directory, string id)
    {
        var separator = id.IndexOf("::", StringComparison.Ordinal);
        if (separator <= 0) throw new InvalidDataException("配置标识无效");
        var path = Path.Combine(directory, id[..separator]);
        var signature = ConfigFiles.Signature(path);
        lock (_sync)
        {
            List<WorkbookView> parsed;
            if (_cache.TryGetValue(path, out var cached) && cached.Signature == signature)
                parsed = cached.Workbooks;
            else
            {
                parsed = string.Equals(Path.GetExtension(path), ".lua", StringComparison.OrdinalIgnoreCase) ? _luaParser.Parse(path) : _workbookParser.Parse(path);
                _cache[path] = new CachedWorkbook(signature, parsed);
            }
            return parsed.FirstOrDefault(workbook => workbook.Id == id) ?? throw new InvalidDataException($"找不到配置页：{id}");
        }
    }

    public List<ReverseReference> FindReverseReferences(
        string directory,
        string value,
        List<string> targetTokens,
        List<string> scalarFields,
        List<string> jsonFields,
        List<RelationRule>? relationRules = null)
    {
        var payload = Load(directory, includeRows: true);
        var wantedTokens = targetTokens.Select(NormalizeRelationToken).Where(token => !string.IsNullOrEmpty(token)).ToHashSet(StringComparer.Ordinal);
        var scalarFieldSet = scalarFields.Select(FieldName).ToHashSet(StringComparer.Ordinal);
        var jsonFieldSet = jsonFields.Select(FieldName).ToHashSet(StringComparer.Ordinal);
        relationRules ??= [];
        var expected = ComparableRelationValue(value);
        var references = new List<ReverseReference>();

        foreach (var workbook in payload.Workbooks.Where(workbook => workbook.Error is null && workbook.Rows.Count > 3))
        {
            var headers = FieldHeaders(workbook);
            var sourceTokens = WorkbookRelationTokens(workbook);
            var sourcePrimaryToken = WorkbookPrimaryRelationToken(workbook);
            var nameColumn = headers.FindIndex(field => FieldName(field) == "name");
            for (var column = 0; column < workbook.ColumnCount; column++)
            {
                if (column >= headers.Count) continue;
                var field = headers[column].Trim();
                if (string.IsNullOrEmpty(field)) continue;
                var lowerField = FieldName(field);
                string? mode = null;
                var tupleIndex = 0;
                var explicitRule = relationRules.FirstOrDefault(rule =>
                    RuleAppliesToWorkbook(rule, sourceTokens) && rule.Fields.Any(candidate => FieldName(candidate) == lowerField));
                if (explicitRule is not null)
                {
                    mode = explicitRule.Mode;
                    tupleIndex = explicitRule.TupleIndex;
                }
                else if (jsonFieldSet.Contains(lowerField)) mode = "jsonKeys";
                else if (scalarFieldSet.Contains(lowerField)) mode = "scalar";
                else if (InferredRelationTarget(field) is string inferred && wantedTokens.Contains(NormalizeRelationToken(inferred))) mode = "scalar";
                else if (IsIdentifierField(field) && wantedTokens.Any(target => target.Length >= 4 && sourcePrimaryToken.StartsWith(target, StringComparison.Ordinal) && sourcePrimaryToken.Length > target.Length)) mode = "scalar";
                else if (sourceTokens.Contains("activity") && lowerField == "subid") mode = "activitySubId";
                if (mode is null) continue;

                for (var row = 3; row < workbook.Rows.Count; row++)
                {
                    if (column >= workbook.Rows[row].Count) continue;
                    var cellValue = workbook.Rows[row][column];
                    var rowName = nameColumn >= 0 && nameColumn < workbook.Rows[row].Count ? workbook.Rows[row][nameColumn] : null;
                    var matches = mode switch
                    {
                        "jsonKeys" => JsonKeys(cellValue).Any(key => ComparableRelationValue(key) == expected),
                        "activitySubId" => wantedTokens.Contains(NormalizeRelationToken(rowName ?? "")) && ComparableRelationValue(cellValue) == expected,
                        "list" => StructuredRelationValues(cellValue, null).Any(item => ComparableRelationValue(item) == expected),
                        "tuple" => StructuredRelationValues(cellValue, tupleIndex).Any(item => ComparableRelationValue(item) == expected),
                        _ => ComparableRelationValue(cellValue) == expected
                    };
                    if (!matches) continue;
                    references.Add(new ReverseReference
                    {
                        BookId = workbook.Id,
                        BookLabel = workbook.SheetCount > 1 ? $"{workbook.Name} · {workbook.SheetName}" : workbook.Name,
                        Field = field,
                        Row = row,
                        Column = column,
                        CellValue = cellValue,
                        RowName = rowName,
                        MatchMode = mode
                    });
                }
            }
        }
        return references;
    }

    public (List<GlobalSearchMatch> Matches, int TotalCount) FindGlobalMatches(string directory, string query, int limit = 500)
    {
        var keyword = query.Trim();
        if (string.IsNullOrEmpty(keyword)) return ([], 0);
        var payload = Load(directory, includeRows: true);
        var matches = new List<GlobalSearchMatch>();
        var totalCount = 0;
        foreach (var workbook in payload.Workbooks.Where(workbook => workbook.Error is null && workbook.Rows.Count > 3))
        {
            var headers = FieldHeaders(workbook);
            var label = workbook.SheetCount > 1 ? $"{workbook.Name} · {workbook.SheetName}" : workbook.Name;
            for (var row = 3; row < workbook.Rows.Count; row++)
            {
                var values = workbook.Rows[row];
                var preview = string.Join(" · ", values.Where(value => !string.IsNullOrWhiteSpace(value)).Take(4));
                var rowPreview = preview.Length > 180 ? preview[..180] : preview;
                for (var column = 0; column < values.Count; column++)
                {
                    var cellValue = values[column];
                    if (cellValue.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    totalCount++;
                    if (matches.Count >= limit) continue;
                    matches.Add(new GlobalSearchMatch
                    {
                        BookId = workbook.Id,
                        BookLabel = label,
                        Field = column < headers.Count && !string.IsNullOrEmpty(headers[column]) ? headers[column] : $"第 {column + 1} 列",
                        Row = row,
                        Column = column,
                        Value = cellValue,
                        RowPreview = rowPreview
                    });
                }
            }
        }
        return (matches, totalCount);
    }

    public void Invalidate(string path)
    {
        lock (_sync) _cache.Remove(path);
    }

    private static WorkbookView Summary(WorkbookView workbook) => new()
    {
        Id = workbook.Id,
        Name = workbook.Name,
        FileName = workbook.FileName,
        SheetName = workbook.SheetName,
        SheetCount = workbook.SheetCount,
        Category = workbook.Category,
        ModifiedAt = workbook.ModifiedAt,
        RowCount = workbook.RowCount,
        ColumnCount = workbook.ColumnCount,
        Rows = [],
        SourceKind = workbook.SourceKind,
        SourceSignature = workbook.SourceSignature,
        EditableFromRow = workbook.EditableFromRow,
        LockedCells = [],
        IsLoaded = false,
        Error = workbook.Error
    };

    private static List<string> FieldHeaders(WorkbookView workbook)
    {
        foreach (var headers in workbook.Rows.Take(3))
        {
            if (headers.Any(IsIdentifierField))
                return headers;
        }
        for (var index = 1; index < Math.Min(3, workbook.Rows.Count); index++)
        {
            var values = workbook.Rows[index].Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
            if (values.Count >= 2 && values.Count(LooksLikeFieldType) >= 2) return workbook.Rows[index - 1];
        }
        return workbook.Rows.Count > 1 ? workbook.Rows[1] : workbook.Rows.FirstOrDefault() ?? [];
    }

    private static string FieldName(string value) => value.Trim().Split('@')[0].Trim().ToLowerInvariant();

    private static bool IsIdentifierField(string value)
    {
        var raw = value.Trim();
        var field = FieldName(raw);
        return field is "id" or "subid" || raw.EndsWith("@id", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeFieldType(string value) => Regex.IsMatch(
        value.Trim(),
        @"^(?:repeated\s+)*(?:u?int(?:8|16|32|64)?|u?long|float|double|bool|boolean|string|json|[a-z][a-z0-9_]*enum|e[a-z][a-z0-9_]*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool RuleAppliesToWorkbook(RelationRule rule, HashSet<string> sourceTokens) =>
        rule.Sources.Count == 0 || rule.Sources.Any(source => sourceTokens.Contains(NormalizeRelationToken(source)));

    private static List<string> ConfigUrls(string directory)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("配置目录不存在，请重新选择目录。");
        return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".lua", StringComparison.OrdinalIgnoreCase))
            .Where(path => !Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal))
            .Where(path => (File.GetAttributes(path) & FileAttributes.Hidden) == 0)
            .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string NormalizeRelationToken(string value)
    {
        var token = Regex.Replace(value, @"\.(xlsx|lua)$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Split('@')[0];
        token = Regex.Replace(token, @"(config|cfg|table|design|column|server)$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Regex.Replace(token, @"[^a-zA-Z0-9]", "", RegexOptions.CultureInvariant).ToLowerInvariant();
    }

    private static HashSet<string> WorkbookRelationTokens(WorkbookView workbook) => new(new[]
    {
        workbook.Name,
        Path.GetFileNameWithoutExtension(workbook.FileName),
        workbook.SheetName,
        workbook.SheetName.Split('@')[0]
    }.Select(NormalizeRelationToken).Where(token => !string.IsNullOrEmpty(token)), StringComparer.Ordinal);

    private static string WorkbookPrimaryRelationToken(WorkbookView workbook)
    {
        var sheetToken = NormalizeRelationToken(workbook.SheetName);
        var nameToken = NormalizeRelationToken(workbook.Name);
        return workbook.SheetCount > 1 && !string.IsNullOrEmpty(sheetToken) && !Regex.IsMatch(sheetToken, @"^sheet\d*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ? sheetToken : nameToken;
    }

    private static string? InferredRelationTarget(string field)
    {
        var target = Regex.Replace(field, @"(?:Config|Cfg)?(?:ID|Id)$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        target = Regex.Replace(target, @"_id$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return target == field || string.IsNullOrEmpty(target) ? null : target;
    }

    private static string ComparableRelationValue(string value)
    {
        var text = value.Trim();
        return !string.IsNullOrEmpty(text) && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var decimalValue)
            // Excel numeric cells often arrive as `901005.0`, while relation
            // cells and user clicks use `901005`. G29 removes only insignificant
            // trailing zeroes and keeps meaningful decimal precision.
            ? decimalValue.ToString("G29", CultureInfo.InvariantCulture)
            : text;
    }

    private static List<string> JsonKeys(string value)
    {
        if (!value.Contains('{')) return [];
        return Regex.Matches(value, @"[""']?([a-zA-Z0-9_.:-]+)[""']?\s*:", RegexOptions.CultureInvariant).Select(match => match.Groups[1].Value).ToList();
    }

    private static List<string> StructuredRelationValues(string value, int? tupleIndex)
    {
        var values = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(value);
            Visit(document.RootElement);
        }
        catch
        {
            if (tupleIndex is int index)
            {
                foreach (Match tuple in Regex.Matches(value, @"\[[^\[\]]*\]", RegexOptions.CultureInvariant))
                {
                    var parts = tuple.Value[1..^1].Split(',');
                    if (index < parts.Length) values.Add(parts[index].Trim().Trim('"', '\''));
                }
            }
            else
            {
                values.AddRange(Regex.Matches(value, @"-?\d+(?:\.\d+)?|[a-zA-Z_][\w.-]*", RegexOptions.CultureInvariant).Select(match => match.Value));
            }
        }
        return values.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.Ordinal).ToList();

        void Visit(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Array) return;
            var children = element.EnumerateArray().ToList();
            var tuple = children.Count > 0 && children.All(child => child.ValueKind is not JsonValueKind.Array and not JsonValueKind.Object);
            if (tuple && tupleIndex is int index && index < children.Count)
            {
                values.Add(PrimitiveText(children[index]));
                return;
            }
            foreach (var child in children)
            {
                if (child.ValueKind == JsonValueKind.Array) Visit(child);
                else if (tupleIndex is null) values.Add(PrimitiveText(child));
            }
        }

        static string PrimitiveText(JsonElement element) => element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? ""
            : element.GetRawText();
    }
}
