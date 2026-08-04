using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using PairPair.ConfigTool.Core;

namespace PairPair.ConfigTool.Windows;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new ConfigToolForm());
    }
}

internal sealed class ConfigToolForm : Form
{
    private sealed class SaveRequest
    {
        public string? Id { get; init; }
        public string? SourceSignature { get; init; }
        public List<CellChange>? Changes { get; init; }
    }

    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private readonly DirectoryLoader _loader = new();
    private readonly ConfigFileSaver _saver = new();
    private readonly GitRepositoryService _gitRepository = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 450 };
    private FileSystemWatcher? _directoryWatcher;
    private string _currentDirectory;
    private string _currentSignature = "";
    private int _refreshGeneration;
    private bool _pageLoaded;

    public ConfigToolForm()
    {
        Text = "PairPair ConfigTool";
        ClientSize = new Size(1380, 860);
        MinimumSize = new Size(960, 620);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_webView);
        _currentDirectory = Settings.LoadDirectory();
        _refreshTimer.Tick += async (_, _) =>
        {
            _refreshTimer.Stop();
            await RefreshAsync(force: false);
        };
        Shown += async (_, _) => await InitializeBrowserAsync();
        Activated += async (_, _) => await RefreshGitStatusAsync();
        FormClosed += (_, _) => _directoryWatcher?.Dispose();
    }

    private async Task InitializeBrowserAsync()
    {
        try
        {
            var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PairPair", "ConfigTool", "WebView2");
            Directory.CreateDirectory(dataDirectory);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: dataDirectory);
            await _webView.EnsureCoreWebView2Async(environment);
            _webView.CoreWebView2.Settings.IsWebMessageEnabled = true;
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _webView.NavigationCompleted += (_, _) => _pageLoaded = true;
            var index = Path.Combine(AppContext.BaseDirectory, "Resources", "index.html");
            if (!File.Exists(index)) throw new FileNotFoundException("应用界面资源缺失，请重新构建 ConfigTool。", index);
            _webView.Source = new Uri(index);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"无法启动内嵌浏览器。请确认已安装 Microsoft Edge WebView2 Runtime。\n\n{error.Message}", "PairPair ConfigTool", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs eventArgs)
    {
        try
        {
            using var document = JsonDocument.Parse(eventArgs.WebMessageAsJson);
            var root = document.RootElement;
            var action = ReadString(root, "action");
            switch (action)
            {
                case "ready":
                case "refresh":
                    _ = RefreshAsync(force: true);
                    break;
                case "chooseDirectory":
                    ChooseDirectory();
                    break;
                case "switchDirectory":
                    SwitchDirectory(ReadString(root, "path"));
                    break;
                case "revealDirectory":
                    RevealDirectory();
                    break;
                case "readClipboard":
                    _ = SendClipboardTextAsync(ReadInt(root, "requestId", -1));
                    break;
                case "writeClipboard":
                    WriteClipboard(ReadString(root, "text"));
                    break;
                case "save":
                    _ = SaveAsync(eventArgs.WebMessageAsJson);
                    break;
                case "loadWorkbook":
                    _ = LoadWorkbookAsync(ReadString(root, "id"));
                    break;
                case "findReverseReferences":
                    _ = FindReverseReferencesAsync(root.Clone());
                    break;
                case "findGlobalMatches":
                    _ = FindGlobalMatchesAsync(root.Clone());
                    break;
                case "refreshGitStatus":
                    _ = RefreshGitStatusAsync();
                    break;
                case "pullGit":
                    _ = PullGitAsync();
                    break;
                case "previewGitClean":
                    _ = PreviewGitCleanAsync();
                    break;
                case "cleanGitChanges":
                    _ = CleanGitChangesAsync(ReadArray(root, "trackedPaths"), ReadArray(root, "untrackedPaths"));
                    break;
            }
        }
        catch (Exception error)
        {
            _ = SendAsync("receiveError", new { message = error.Message });
        }
    }

    private async Task RefreshAsync(bool force)
    {
        if (string.IsNullOrWhiteSpace(_currentDirectory) || !Directory.Exists(_currentDirectory))
        {
            await SendAsync("receiveError", new { message = "尚未选择配置目录，请选择包含 .xlsx 或 .lua 配置的目录。" });
            return;
        }
        var signature = _loader.SignatureForDirectory(_currentDirectory);
        if (!force && signature == _currentSignature) return;
        var generation = ++_refreshGeneration;
        var directory = _currentDirectory;
        await SendAsync("setLoading", new { loading = true });
        try
        {
            var payload = await Task.Run(() => _loader.Load(directory));
            if (generation != _refreshGeneration || !string.Equals(directory, _currentDirectory, StringComparison.Ordinal)) return;
            _currentSignature = signature;
            await SendAsync("receiveData", payload);
            await RefreshGitStatusAsync();
            WatchDirectory(directory);
        }
        catch (Exception error)
        {
            if (generation == _refreshGeneration && string.Equals(directory, _currentDirectory, StringComparison.Ordinal))
                await SendAsync("receiveError", new { message = error.Message });
        }
    }

    private async Task SaveAsync(string json)
    {
        var request = JsonSerializer.Deserialize<SaveRequest>(json, JsonDefaults.Options);
        if (request?.Id is null || request.SourceSignature is null || request.Changes is not { Count: > 0 })
        {
            await SendAsync("saveFailed", new { message = "没有可保存的修改。" });
            return;
        }
        var directory = _currentDirectory;
        await SendAsync("setSaving", new { saving = true });
        try
        {
            await Task.Run(() =>
            {
                _saver.Save(directory, request.Id, request.SourceSignature, request.Changes);
                _loader.Invalidate(Path.Combine(directory, request.Id[..request.Id.IndexOf("::", StringComparison.Ordinal)]));
            });
            if (!string.Equals(directory, _currentDirectory, StringComparison.Ordinal)) return;
            _currentSignature = "";
            await SendAsync("saveSucceeded", new { id = request.Id });
            await RefreshAsync(force: true);
        }
        catch (Exception error)
        {
            if (string.Equals(directory, _currentDirectory, StringComparison.Ordinal)) await SendAsync("saveFailed", new { message = error.Message });
        }
    }

    private async Task LoadWorkbookAsync(string id)
    {
        var directory = _currentDirectory;
        try
        {
            var workbook = await Task.Run(() => _loader.LoadWorkbook(directory, id));
            if (string.Equals(directory, _currentDirectory, StringComparison.Ordinal)) await SendAsync("receiveWorkbook", workbook);
        }
        catch (Exception error)
        {
            if (string.Equals(directory, _currentDirectory, StringComparison.Ordinal)) await SendAsync("receiveWorkbookError", new { id, message = error.Message });
        }
    }

    private async Task FindReverseReferencesAsync(JsonElement request)
    {
        var requestId = ReadInt(request, "requestId", -1);
        var value = ReadString(request, "value");
        var directory = _currentDirectory;
        try
        {
            var references = await Task.Run(() => _loader.FindReverseReferences(directory, value, ReadArray(request, "targetTokens"), ReadArray(request, "scalarFields"), ReadArray(request, "jsonFields")));
            if (string.Equals(directory, _currentDirectory, StringComparison.Ordinal))
                await SendAsync("receiveReverseReferences", new ReverseReferenceResponse { RequestId = requestId, Value = value, References = references });
        }
        catch (Exception error)
        {
            if (string.Equals(directory, _currentDirectory, StringComparison.Ordinal)) await SendAsync("receiveReverseReferenceError", new { requestId, message = error.Message });
        }
    }

    private async Task FindGlobalMatchesAsync(JsonElement request)
    {
        var requestId = ReadInt(request, "requestId", -1);
        var query = ReadString(request, "query");
        var directory = _currentDirectory;
        try
        {
            var result = await Task.Run(() => _loader.FindGlobalMatches(directory, query));
            if (string.Equals(directory, _currentDirectory, StringComparison.Ordinal))
                await SendAsync("receiveGlobalSearchResults", new GlobalSearchResponse { RequestId = requestId, Query = query, TotalCount = result.TotalCount, Matches = result.Matches });
        }
        catch (Exception error)
        {
            if (string.Equals(directory, _currentDirectory, StringComparison.Ordinal)) await SendAsync("receiveGlobalSearchError", new { requestId, message = error.Message });
        }
    }

    private async Task SendClipboardTextAsync(int requestId)
    {
        var text = "";
        try
        {
            if (Clipboard.ContainsText()) text = Clipboard.GetText();
        }
        catch { }
        await SendAsync("receiveClipboardText", new { requestId, text });
    }

    private static void WriteClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch { }
    }

    private async Task RefreshGitStatusAsync()
    {
        var directory = _currentDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        var status = await Task.Run(() => _gitRepository.Inspect(directory));
        if (string.Equals(directory, _currentDirectory, StringComparison.Ordinal)) await SendAsync("receiveGitStatus", status);
    }

    private async Task PullGitAsync()
    {
        var directory = _currentDirectory;
        await SendAsync("setGitOperation", new { operation = "pull", running = true });
        var result = await Task.Run(() => _gitRepository.Pull(directory));
        if (!string.Equals(directory, _currentDirectory, StringComparison.Ordinal)) return;
        await SendAsync("receiveGitOperation", new { operation = "pull", success = result.Success, message = result.Message, status = result.Status });
        if (result.Success)
        {
            _currentSignature = "";
            await RefreshAsync(force: true);
        }
    }

    private async Task PreviewGitCleanAsync()
    {
        var directory = _currentDirectory;
        await SendAsync("setGitOperation", new { operation = "previewClean", running = true });
        var preview = await Task.Run(() => _gitRepository.PreviewClean(directory));
        if (string.Equals(directory, _currentDirectory, StringComparison.Ordinal)) await SendAsync("receiveGitCleanPreview", preview);
    }

    private async Task CleanGitChangesAsync(List<string> trackedPaths, List<string> untrackedPaths)
    {
        var directory = _currentDirectory;
        await SendAsync("setGitOperation", new { operation = "clean", running = true });
        var result = await Task.Run(() => _gitRepository.Clean(directory, trackedPaths, untrackedPaths));
        if (!string.Equals(directory, _currentDirectory, StringComparison.Ordinal)) return;
        await SendAsync("receiveGitOperation", new { operation = "clean", success = result.Success, message = result.Message, status = result.Status });
        if (result.Success)
        {
            _currentSignature = "";
            await RefreshAsync(force: true);
        }
    }

    private void ChooseDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择包含 .xlsx 或 .lua 配置的目录",
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(_currentDirectory) ? _currentDirectory : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) SwitchDirectory(dialog.SelectedPath);
    }

    private void SwitchDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            _ = SendAsync("directorySwitchFailed", new { message = $"预设目录不存在或不是文件夹：{path}" });
            return;
        }
        _currentDirectory = Path.GetFullPath(path);
        _currentSignature = "";
        Settings.SaveDirectory(_currentDirectory);
        _ = RefreshAsync(force: true);
    }

    private void RevealDirectory()
    {
        if (Directory.Exists(_currentDirectory)) Process.Start(new ProcessStartInfo { FileName = _currentDirectory, UseShellExecute = true });
    }

    private void WatchDirectory(string directory)
    {
        _directoryWatcher?.Dispose();
        _directoryWatcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName,
            EnableRaisingEvents = true
        };
        _directoryWatcher.Changed += QueueRefresh;
        _directoryWatcher.Created += QueueRefresh;
        _directoryWatcher.Deleted += QueueRefresh;
        _directoryWatcher.Renamed += QueueRefresh;
    }

    private void QueueRefresh(object? sender, FileSystemEventArgs eventArgs)
    {
        if (IsDisposed || Disposing) return;
        BeginInvoke(() =>
        {
            _refreshTimer.Stop();
            _refreshTimer.Start();
        });
    }

    private async Task SendAsync(string function, object payload)
    {
        if (!_pageLoaded || _webView.CoreWebView2 is null || IsDisposed) return;
        var json = JsonSerializer.Serialize(payload, JsonDefaults.Options);
        try { await _webView.CoreWebView2.ExecuteScriptAsync($"window.ConfigTool?.{function}({json});"); }
        catch (InvalidOperationException) { }
    }

    private static string ReadString(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static int ReadInt(JsonElement element, string property, int fallback) => element.TryGetProperty(property, out var value) && value.TryGetInt32(out var parsed) ? parsed : fallback;
    private static List<string> ReadArray(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString() ?? "").ToList() : [];
}

internal static class Settings
{
    private static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PairPair", "ConfigTool", "settings.json");

    public static string LoadDirectory()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            return document.RootElement.TryGetProperty("configDirectory", out var directory) ? directory.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    public static void SaveDirectory(string directory)
    {
        var folder = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(folder);
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { configDirectory = directory }, JsonDefaults.Options));
        File.Move(temporary, SettingsPath, overwrite: true);
    }
}
