using System.Globalization;
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

    public List<ReverseReference> FindReverseReferences(string directory, string value, List<string> targetTokens, List<string> scalarFields, List<string> jsonFields)
    {
        var payload = Load(directory, includeRows: true);
        var wantedTokens = targetTokens.Select(NormalizeRelationToken).Where(token => !string.IsNullOrEmpty(token)).ToHashSet(StringComparer.Ordinal);
        var scalarFieldSet = scalarFields.Select(field => field.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var jsonFieldSet = jsonFields.Select(field => field.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var expected = ComparableRelationValue(value);
        var references = new List<ReverseReference>();

        foreach (var workbook in payload.Workbooks.Where(workbook => workbook.Error is null && workbook.Rows.Count > 3))
        {
            var headers = workbook.Rows.Count > 1 ? workbook.Rows[1] : [];
            var sourceTokens = WorkbookRelationTokens(workbook);
            var sourcePrimaryToken = WorkbookPrimaryRelationToken(workbook);
            var nameColumn = headers.FindIndex(field => string.Equals(field.Trim(), "name", StringComparison.OrdinalIgnoreCase));
            for (var column = 0; column < workbook.ColumnCount; column++)
            {
                if (column >= headers.Count) continue;
                var field = headers[column].Trim();
                if (string.IsNullOrEmpty(field)) continue;
                var lowerField = field.ToLowerInvariant();
                string? mode = null;
                if (jsonFieldSet.Contains(lowerField)) mode = "jsonKeys";
                else if (scalarFieldSet.Contains(lowerField)) mode = "scalar";
                else if (InferredRelationTarget(field) is string inferred && wantedTokens.Contains(NormalizeRelationToken(inferred))) mode = "scalar";
                else if ((lowerField is "id" or "subid") && wantedTokens.Any(target => target.Length >= 4 && sourcePrimaryToken.StartsWith(target, StringComparison.Ordinal) && sourcePrimaryToken.Length > target.Length)) mode = "scalar";
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
            var headers = workbook.Rows.Count > 1 ? workbook.Rows[1] : [];
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
            ? decimalValue.ToString(CultureInfo.InvariantCulture)
            : text;
    }

    private static List<string> JsonKeys(string value)
    {
        if (!value.Contains('{')) return [];
        return Regex.Matches(value, @"[""']?([a-zA-Z0-9_.:-]+)[""']?\s*:", RegexOptions.CultureInvariant).Select(match => match.Groups[1].Value).ToList();
    }
}
