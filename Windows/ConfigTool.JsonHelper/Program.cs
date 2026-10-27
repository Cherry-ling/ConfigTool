using System.Text.Json;
using PairPair.ConfigTool.Core;

try
{
    using var request = JsonDocument.Parse(Console.In.ReadToEnd());
    var root = request.RootElement;
    var path = root.GetProperty("path").GetString()!;
    var sheet = root.GetProperty("sheetName").GetString()!;
    var original = Convert.FromBase64String(root.GetProperty("original").GetString()!);
    var binaryProperty = root.GetProperty("originalBinary");
    var originalBinary = binaryProperty.ValueKind == JsonValueKind.Null ? null : Convert.FromBase64String(binaryProperty.GetString()!);
    var changes = root.GetProperty("changes").EnumerateArray().Select(change => new CellChange
    {
        Row = change.GetProperty("row").GetInt32(), Column = change.GetProperty("column").GetInt32(), Value = change.GetProperty("value").GetString()!
    }).ToList();
    JsonConfigEditor.Save(path, sheet, changes, expectedContents: original, expectedBinaryContents: originalBinary, checkBinaryContents: true);
    Console.WriteLine("ok");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return error is ConfigSaveException { SourceChanged: true } ? 3 : 1;
}
