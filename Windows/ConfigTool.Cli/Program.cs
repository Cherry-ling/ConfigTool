using System.Text.Json;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using System.Diagnostics;
using PairPair.ConfigTool.Core;

if (args.Length == 1 && args[0] == "--self-test")
{
    return RunSelfTest();
}

if (args.Length < 2)
{
    Console.Error.WriteLine("用法：ConfigTool.Cli --self-test | --audit <配置目录> | --global-search-audit <配置目录> <关键词> | --git-audit <配置目录>");
    return 1;
}

var loader = new DirectoryLoader();
try
{
    switch (args[0])
    {
        case "--audit":
        {
            var payload = loader.Load(args[1], includeRows: true);
            var failures = payload.Workbooks.Where(workbook => workbook.Error is not null).ToList();
            var rows = payload.Workbooks.Sum(workbook => workbook.RowCount);
            var cells = payload.Workbooks.Sum(workbook => workbook.Rows.Sum(row => row.Count(value => !string.IsNullOrEmpty(value))));
            Console.WriteLine($"files={payload.FileCount} sheets={payload.Workbooks.Count} rows={rows} nonEmptyCells={cells} failures={failures.Count}");
            foreach (var failure in failures) Console.WriteLine($"ERROR {failure.FileName}: {failure.Error}");
            return failures.Count == 0 ? 0 : 2;
        }
        case "--global-search-audit" when args.Length >= 3:
        {
            var result = loader.FindGlobalMatches(args[1], args[2]);
            Console.WriteLine(JsonSerializer.Serialize(new GlobalSearchResponse
            {
                RequestId = 0,
                Query = args[2],
                TotalCount = result.TotalCount,
                Matches = result.Matches
            }, JsonDefaults.Options));
            return 0;
        }
        case "--git-audit" when args.Length >= 2:
        {
            var status = new GitRepositoryService().Inspect(args[1]);
            Console.WriteLine(JsonSerializer.Serialize(status, JsonDefaults.Options));
            return status.IsRepository ? 0 : 2;
        }
        default:
            Console.Error.WriteLine("参数无效。可用：--audit、--global-search-audit、--git-audit");
            return 1;
    }
}
catch (Exception error)
{
    Console.Error.WriteLine($"ConfigTool audit failed: {error.Message}");
    return 1;
}

static int RunSelfTest()
{
    var directory = Path.Combine(Path.GetTempPath(), $"PairPairConfigTool-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var luaPath = Path.Combine(directory, "DailyQuestConf.lua");
        File.WriteAllText(luaPath, """
            local tmp = {
                ["DailyQuestMap"] = {
                    [101] = { Id = 101, Name = "before", Active = true, Reward = { 43, 2, 0, 9 } },
                    [102] = { Id = 102, Name = "second", Active = false, Reward = { 43, 3, 0, 9 } },
                },
            }
            return tmp["DailyQuestMap"]
            """, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var excelPath = Path.Combine(directory, "Activity.xlsx");
        CreateWorkbook(excelPath);

        var loader = new DirectoryLoader();
        var initial = loader.Load(directory, includeRows: true);
        Assert(initial.FileCount == 2 && initial.Workbooks.Count == 2, "配置扫描数量不正确");
        var lua = initial.Workbooks.Single(book => book.SourceKind == "lua");
        var excel = initial.Workbooks.Single(book => book.SourceKind == "xlsx");
        Assert(lua.Rows[3][2] == "before", "Lua 读取值不正确");
        Assert(excel.Rows[3][1] == "before", "Excel 读取值不正确");

        var saver = new ConfigFileSaver();
        saver.Save(directory, lua.Id, lua.SourceSignature, [new CellChange { Row = 3, Column = 2, Value = "after" }]);
        loader.Invalidate(luaPath);
        Assert(loader.LoadWorkbook(directory, lua.Id).Rows[3][2] == "after", "Lua 保存后未正确回读");
        try
        {
            saver.Save(directory, lua.Id, lua.SourceSignature, [new CellChange { Row = 3, Column = 2, Value = "stale" }]);
            throw new InvalidOperationException("Lua 旧签名未被拒绝");
        }
        catch (ConfigSaveException error) when (error.SourceChanged) { }

        saver.Save(directory, excel.Id, excel.SourceSignature, [new CellChange { Row = 3, Column = 1, Value = "updated" }]);
        loader.Invalidate(excelPath);
        Assert(loader.LoadWorkbook(directory, excel.Id).Rows[3][1] == "updated", "Excel 保存后未正确回读");
        var global = loader.FindGlobalMatches(directory, "updated");
        Assert(global.TotalCount == 1 && global.Matches.Single().BookId == excel.Id, "全局搜索结果不正确");

        var gitDirectory = Path.Combine(directory, "git-self-test");
        Directory.CreateDirectory(gitDirectory);
        RunGitForSelfTest(gitDirectory, "init", "-q");
        RunGitForSelfTest(gitDirectory, "config", "user.email", "configtool-self-test@example.invalid");
        RunGitForSelfTest(gitDirectory, "config", "user.name", "ConfigTool Self Test");
        var trackedPath = Path.Combine(gitDirectory, "tracked.txt");
        var trackedKeepPath = Path.Combine(gitDirectory, "tracked-keep.txt");
        var untrackedPath = Path.Combine(gitDirectory, "untracked.txt");
        var untrackedKeepPath = Path.Combine(gitDirectory, "untracked-keep.txt");
        File.WriteAllText(trackedPath, "before\n");
        File.WriteAllText(trackedKeepPath, "keep-before\n");
        RunGitForSelfTest(gitDirectory, "add", "tracked.txt", "tracked-keep.txt");
        RunGitForSelfTest(gitDirectory, "commit", "-qm", "initial");
        File.WriteAllText(trackedPath, "changed\n");
        File.WriteAllText(trackedKeepPath, "keep-changed\n");
        File.WriteAllText(untrackedPath, "untracked\n");
        File.WriteAllText(untrackedKeepPath, "keep-untracked\n");
        var git = new GitRepositoryService();
        var dirty = git.Inspect(gitDirectory);
        Assert(dirty.IsRepository && dirty.TrackedChangeCount == 2 && dirty.UntrackedCount == 2 && !dirty.CanPull, "Git 工作区状态识别不正确");
        var preview = git.PreviewClean(gitDirectory);
        Assert(preview.UntrackedPaths.Contains("untracked.txt") && preview.UntrackedPaths.Contains("untracked-keep.txt"), "Git 未跟踪文件预览不正确");
        var restore = git.Clean(gitDirectory, ["tracked.txt"], []);
        Assert(restore.Success && File.ReadAllText(trackedPath) == "before\n" && File.ReadAllText(trackedKeepPath) == "keep-changed\n" && File.Exists(untrackedPath), "Git 单文件恢复不正确");
        var delete = git.Clean(gitDirectory, [], ["untracked.txt"]);
        Assert(delete.Success && !File.Exists(untrackedPath) && File.Exists(untrackedKeepPath) && File.ReadAllText(trackedKeepPath) == "keep-changed\n", "Git 单文件删除不正确");
        var finishClean = git.Clean(gitDirectory, ["tracked-keep.txt"], ["untracked-keep.txt"]);
        Assert(finishClean.Success && File.ReadAllText(trackedKeepPath) == "keep-before\n" && !File.Exists(untrackedKeepPath), "Git 选择清理收尾不正确");

        var bareRemote = Path.Combine(directory, "git-remote.git");
        var producer = Path.Combine(directory, "git-producer");
        var consumer = Path.Combine(directory, "git-consumer");
        RunGitForSelfTest(directory, "init", "--bare", "-q", bareRemote);
        Directory.CreateDirectory(producer);
        RunGitForSelfTest(producer, "init", "-q");
        RunGitForSelfTest(producer, "config", "user.email", "configtool-self-test@example.invalid");
        RunGitForSelfTest(producer, "config", "user.name", "ConfigTool Self Test");
        var sharedPath = Path.Combine(producer, "shared.txt");
        File.WriteAllText(sharedPath, "first\n");
        RunGitForSelfTest(producer, "add", "shared.txt");
        RunGitForSelfTest(producer, "commit", "-qm", "first");
        RunGitForSelfTest(producer, "branch", "-M", "main");
        RunGitForSelfTest(producer, "remote", "add", "origin", bareRemote);
        RunGitForSelfTest(producer, "push", "-qu", "origin", "main");
        RunGitForSelfTest(directory, "--git-dir", bareRemote, "symbolic-ref", "HEAD", "refs/heads/main");
        RunGitForSelfTest(directory, "clone", "-q", bareRemote, consumer);
        File.WriteAllText(sharedPath, "second\n");
        RunGitForSelfTest(producer, "commit", "-am", "second", "-q");
        RunGitForSelfTest(producer, "push", "-q", "origin", "main");
        var pull = git.Pull(consumer);
        Assert(pull.Success && File.ReadAllText(Path.Combine(consumer, "shared.txt")) == "second\n", "Git 快进拉取不正确");

        Console.WriteLine("self_test=ok lua_read_save=ok xlsx_read_save=ok conflict_guard=ok global_search=ok git_clean=ok git_pull=ok");
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"ConfigTool self test failed: {error.Message}");
        return 1;
    }
    finally
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

static void CreateWorkbook(string path)
{
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    Add(archive, "[Content_Types].xml", """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
          <Default Extension="xml" ContentType="application/xml"/>
          <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
          <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
        </Types>
        """);
    Add(archive, "_rels/.rels", """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
        </Relationships>
        """);
    Add(archive, "xl/workbook.xml", """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
          <sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets>
        </workbook>
        """);
    Add(archive, "xl/_rels/workbook.xml.rels", """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
        </Relationships>
        """);
    Add(archive, "xl/worksheets/sheet1.xml", """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <sheetData>
            <row r="1"><c r="A1" t="inlineStr"><is><t>说明</t></is></c><c r="B1" t="inlineStr"><is><t>说明</t></is></c><c r="C1" t="inlineStr"><is><t>说明</t></is></c></row>
            <row r="2"><c r="A2" t="inlineStr"><is><t>ID</t></is></c><c r="B2" t="inlineStr"><is><t>Name</t></is></c><c r="C2" t="inlineStr"><is><t>Value</t></is></c></row>
            <row r="3"><c r="A3" t="inlineStr"><is><t>int</t></is></c><c r="B3" t="inlineStr"><is><t>string</t></is></c><c r="C3" t="inlineStr"><is><t>int</t></is></c></row>
            <row r="4"><c r="A4"><v>101</v></c><c r="B4" t="inlineStr"><is><t>before</t></is></c><c r="C4"><v>42</v></c></row>
          </sheetData>
        </worksheet>
        """);
}

static void Add(ZipArchive archive, string path, string value)
{
    var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
    using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    writer.Write(value);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void RunGitForSelfTest(string directory, params string[] arguments)
{
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        }
    };
    foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
    if (!process.Start()) throw new InvalidOperationException("无法启动 Git 自检命令");
    process.WaitForExit();
    if (process.ExitCode == 0) return;
    throw new InvalidOperationException($"Git 自检命令失败：{process.StandardError.ReadToEnd().Trim()}");
}
