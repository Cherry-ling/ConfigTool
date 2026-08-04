using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PairPair.ConfigTool.Core;

public sealed class WorkbookView
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string FileName { get; init; }
    public required string SheetName { get; init; }
    public required int SheetCount { get; init; }
    public required string Category { get; init; }
    public required string ModifiedAt { get; init; }
    public required int RowCount { get; init; }
    public required int ColumnCount { get; init; }
    public required List<List<string>> Rows { get; init; }
    public required string SourceKind { get; init; }
    public required string SourceSignature { get; init; }
    public required int EditableFromRow { get; init; }
    public required List<string> LockedCells { get; init; }
    public required bool IsLoaded { get; init; }
    public string? Error { get; init; }
}

public sealed class DirectoryPayload
{
    public required string Directory { get; init; }
    public required string ScannedAt { get; init; }
    public required int FileCount { get; init; }
    public required List<WorkbookView> Workbooks { get; init; }
}

public sealed class CellChange
{
    public int Row { get; init; }
    public int Column { get; init; }
    public string Value { get; init; } = "";
}

public sealed class ReverseReference
{
    public required string BookId { get; init; }
    public required string BookLabel { get; init; }
    public required string Field { get; init; }
    public required int Row { get; init; }
    public required int Column { get; init; }
    public required string CellValue { get; init; }
    public string? RowName { get; init; }
    public required string MatchMode { get; init; }
}

public sealed class ReverseReferenceResponse
{
    public int RequestId { get; init; }
    public required string Value { get; init; }
    public required List<ReverseReference> References { get; init; }
}

public sealed class RelationRule
{
    public List<string> Sources { get; init; } = [];
    public List<string> Fields { get; init; } = [];
    public string Mode { get; init; } = "scalar";
    public int TupleIndex { get; init; }
}

public sealed class GlobalSearchMatch
{
    public required string BookId { get; init; }
    public required string BookLabel { get; init; }
    public required string Field { get; init; }
    public required int Row { get; init; }
    public required int Column { get; init; }
    public required string Value { get; init; }
    public required string RowPreview { get; init; }
}

public sealed class GlobalSearchResponse
{
    public int RequestId { get; init; }
    public required string Query { get; init; }
    public int TotalCount { get; init; }
    public required List<GlobalSearchMatch> Matches { get; init; }
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default
    };
}

public static class ConfigCategory
{
    public static string NameFor(string table)
    {
        var lower = table.ToLowerInvariant();
        if (lower.StartsWith("activity") || new[] { "battlepass", "weeklyrank", "bot" }.Contains(lower)) return "活动";
        if (lower.Contains("level") || lower.Contains("creator") || lower.Contains("feature")) return "关卡";
        if (lower.Contains("reward") || lower.Contains("shop") || lower.Contains("payment") || lower.Contains("chest")) return "奖励与商业化";
        if (lower.Contains("item") || lower.Contains("profile") || lower.Contains("avatar") || lower.Contains("badge")) return "物品与角色";
        return "基础配置";
    }
}

public static class ConfigFiles
{
    public static string Signature(string path)
    {
        var info = new FileInfo(path);
        return $"{info.LastWriteTimeUtc.Ticks}|{info.Length}";
    }

    public static string IsoTimestamp(DateTime value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}

public sealed class ConfigSaveException(string message, bool sourceChanged = false) : Exception(message)
{
    public bool SourceChanged { get; } = sourceChanged;

    public static ConfigSaveException SourceChangedException() => new("源文件已被外部修改。为避免覆盖新内容，请先刷新后重新编辑。", true);
}
