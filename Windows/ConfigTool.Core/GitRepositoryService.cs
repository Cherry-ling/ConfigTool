using System.ComponentModel;
using System.Diagnostics;

namespace PairPair.ConfigTool.Core;

public sealed class GitChange
{
    public required string Path { get; init; }
    public required string Kind { get; init; }
    public required string Status { get; init; }
}

public sealed class GitRepositoryStatus
{
    public bool IsRepository { get; init; }
    public bool IsGitAvailable { get; init; } = true;
    public string RepositoryRoot { get; init; } = "";
    public string RemoteName { get; init; } = "";
    public string RemoteUrl { get; init; } = "";
    public string Branch { get; init; } = "";
    public string Upstream { get; init; } = "";
    public bool IsDetached { get; init; }
    public bool HasConflicts { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public int TrackedChangeCount { get; init; }
    public int UntrackedCount { get; init; }
    public bool CanPull { get; init; }
    public bool CanClean { get; init; }
    public string State { get; init; } = "inactive";
    public string Message { get; init; } = "";
    public List<GitChange> Changes { get; init; } = [];
}

public sealed class GitCleanPreview
{
    public required GitRepositoryStatus Status { get; init; }
    public required List<string> UntrackedPaths { get; init; }
    public string Message { get; init; } = "";
}

public sealed class GitOperationResult
{
    public required bool Success { get; init; }
    public required string Message { get; init; }
    public required GitRepositoryStatus Status { get; init; }
}

public sealed class GitRepositoryService
{
    private const int CommandTimeoutMilliseconds = 30_000;

    public GitRepositoryStatus Inspect(string directory)
    {
        var rootResult = RunGit(directory, "rev-parse", "--show-toplevel");
        if (!rootResult.Started)
        {
            return new GitRepositoryStatus
            {
                IsRepository = false,
                IsGitAvailable = false,
                CanClean = false,
                State = "error",
                Message = "未找到 Git 命令。请安装 Git for Windows 后重试。"
            };
        }
        if (!rootResult.Success)
        {
            return new GitRepositoryStatus
            {
                IsRepository = false,
                CanClean = false,
                State = "inactive",
                Message = "当前配置目录不属于 Git 仓库。"
            };
        }

        var root = rootResult.StdOut.Trim();
        if (string.IsNullOrEmpty(root))
        {
            return new GitRepositoryStatus
            {
                IsRepository = false,
                CanClean = false,
                State = "inactive",
                Message = "当前配置目录不属于 Git 仓库。"
            };
        }

        var branchResult = RunGit(root, "branch", "--show-current");
        var branch = branchResult.Success ? branchResult.StdOut.Trim() : "";
        var isDetached = string.IsNullOrEmpty(branch);
        var upstreamResult = isDetached
            ? GitCommandResult.EmptySuccess
            : RunGit(root, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}");
        var upstream = upstreamResult.Success ? upstreamResult.StdOut.Trim() : "";

        var remoteName = "";
        if (!isDetached)
        {
            var remoteResult = RunGit(root, "config", "--get", $"branch.{branch}.remote");
            if (remoteResult.Success) remoteName = remoteResult.StdOut.Trim();
        }
        var remoteUrl = "";
        if (!string.IsNullOrEmpty(remoteName))
        {
            var urlResult = RunGit(root, "remote", "get-url", remoteName);
            if (urlResult.Success) remoteUrl = urlResult.StdOut.Trim();
        }

        var statusResult = RunGit(root, "status", "--porcelain=v1", "--untracked-files=all");
        if (!statusResult.Success)
        {
            return new GitRepositoryStatus
            {
                IsRepository = true,
                RepositoryRoot = root,
                RemoteName = remoteName,
                RemoteUrl = remoteUrl,
                Branch = branch,
                Upstream = upstream,
                IsDetached = isDetached,
                CanClean = false,
                State = "error",
                Message = GitError("无法读取 Git 工作区状态", statusResult)
            };
        }

        var changes = ParseChanges(statusResult.StdOut);
        var trackedCount = changes.Count(change => change.Kind != "untracked");
        var untrackedCount = changes.Count(change => change.Kind == "untracked");
        var hasConflicts = changes.Any(change => IsConflictStatus(change.Status));
        var ahead = 0;
        var behind = 0;
        if (!string.IsNullOrEmpty(upstream))
        {
            var distanceResult = RunGit(root, "rev-list", "--left-right", "--count", "HEAD...@{u}");
            if (distanceResult.Success)
            {
                var counts = distanceResult.StdOut.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (counts.Length == 2)
                {
                    _ = int.TryParse(counts[0], out ahead);
                    _ = int.TryParse(counts[1], out behind);
                }
            }
        }

        var canPull = !isDetached && !string.IsNullOrEmpty(upstream) && !hasConflicts && trackedCount == 0 && untrackedCount == 0 && ahead == 0;
        return new GitRepositoryStatus
        {
            IsRepository = true,
            RepositoryRoot = root,
            RemoteName = remoteName,
            RemoteUrl = remoteUrl,
            Branch = branch,
            Upstream = upstream,
            IsDetached = isDetached,
            HasConflicts = hasConflicts,
            Ahead = ahead,
            Behind = behind,
            TrackedChangeCount = trackedCount,
            UntrackedCount = untrackedCount,
            CanPull = canPull,
            CanClean = !hasConflicts,
            State = StateFor(isDetached, upstream, hasConflicts, trackedCount, untrackedCount, ahead, behind),
            Message = MessageFor(isDetached, upstream, hasConflicts, trackedCount, untrackedCount, ahead, behind),
            Changes = changes
        };
    }

    public GitCleanPreview PreviewClean(string directory)
    {
        var status = Inspect(directory);
        if (!status.IsRepository || !status.CanClean)
        {
            return new GitCleanPreview { Status = status, UntrackedPaths = [], Message = status.Message };
        }
        var previewResult = RunGit(status.RepositoryRoot, "clean", "-nd");
        if (!previewResult.Success)
        {
            return new GitCleanPreview
            {
                Status = status,
                UntrackedPaths = [],
                Message = GitError("无法预览未跟踪文件", previewResult)
            };
        }
        return new GitCleanPreview
        {
            Status = status,
            UntrackedPaths = previewResult.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("Would remove ", StringComparison.Ordinal))
                .Select(line => line["Would remove ".Length..])
                .ToList()
        };
    }

    public GitOperationResult Pull(string directory)
    {
        var initial = Inspect(directory);
        if (!initial.IsRepository)
            return Failure(initial, initial.Message);

        var pullResult = RunGit(initial.RepositoryRoot, "pull", "--ff-only");
        if (!pullResult.Success)
            return Failure(Inspect(directory), GitError("拉取失败", pullResult));

        return new GitOperationResult
        {
            Success = true,
            Message = initial.Behind > 0 ? $"已快进拉取 {initial.Behind} 个提交，正在刷新配置。" : "代码已是最新，正在刷新配置。",
            Status = Inspect(directory)
        };
    }

    public GitOperationResult Clean(string directory, IReadOnlyCollection<string> trackedPaths, IReadOnlyCollection<string> untrackedPaths)
    {
        var before = Inspect(directory);
        if (!before.IsRepository || !before.CanClean)
            return Failure(before, before.Message);

        var selectedTracked = SelectAllowedPaths(
            trackedPaths,
            before.Changes.Where(change => change.Kind == "tracked").Select(change => change.Path)
        );
        var untrackedPreview = PreviewClean(directory);
        if (!string.IsNullOrEmpty(untrackedPreview.Message))
            return Failure(untrackedPreview.Status, untrackedPreview.Message);
        var selectedUntracked = SelectAllowedPaths(untrackedPaths, untrackedPreview.UntrackedPaths);
        if (selectedTracked.Count == 0 && selectedUntracked.Count == 0)
            return Failure(before, "请至少选择一个要清理的文件或目录。");

        if (selectedTracked.Count > 0)
        {
            var restoreArguments = new List<string> { "restore", "--source=HEAD", "--staged", "--worktree", "--" };
            restoreArguments.AddRange(selectedTracked);
            var restoreResult = RunGit(before.RepositoryRoot, [.. restoreArguments]);
            if (!restoreResult.Success)
                return Failure(Inspect(directory), GitError("恢复已跟踪文件失败", restoreResult));
        }

        if (selectedUntracked.Count > 0)
        {
            var cleanArguments = new List<string> { "clean", "-df", "--" };
            cleanArguments.AddRange(selectedUntracked);
            var cleanResult = RunGit(before.RepositoryRoot, [.. cleanArguments]);
            if (!cleanResult.Success)
                return Failure(Inspect(directory), GitError("删除未跟踪文件失败", cleanResult));
        }

        var status = Inspect(directory);
        var parts = new List<string>();
        if (selectedTracked.Count > 0) parts.Add($"已恢复 {selectedTracked.Count} 项已跟踪改动");
        if (selectedUntracked.Count > 0) parts.Add($"已删除 {selectedUntracked.Count} 项未跟踪文件或目录");
        var message = string.Join("，", parts) + "；Git 忽略文件未删除。";
        return new GitOperationResult { Success = true, Message = message, Status = status };
    }

    private static List<string> SelectAllowedPaths(IEnumerable<string> selectedPaths, IEnumerable<string> allowedPaths)
    {
        var allowed = new HashSet<string>(allowedPaths, StringComparer.Ordinal);
        return selectedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path) && allowed.Contains(path))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static List<GitChange> ParseChanges(string output)
    {
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Length >= 3)
            .Select(line =>
            {
                var status = line[..2];
                return new GitChange
                {
                    Status = status,
                    Kind = status == "??" ? "untracked" : IsConflictStatus(status) ? "conflict" : "tracked",
                    Path = line[3..].Trim()
                };
            })
            .ToList();
    }

    private static bool IsConflictStatus(string status) => status.Contains('U') || status is "AA" or "DD";

    private static string StateFor(bool isDetached, string upstream, bool conflicts, int tracked, int untracked, int ahead, int behind)
    {
        if (conflicts || isDetached || string.IsNullOrEmpty(upstream) || tracked + untracked > 0 || ahead > 0) return "warning";
        return behind > 0 ? "ready" : "synced";
    }

    private static string MessageFor(bool isDetached, string upstream, bool conflicts, int tracked, int untracked, int ahead, int behind)
    {
        if (conflicts) return "存在未解决冲突，请先在 Git 工具中处理。";
        if (isDetached) return "当前处于游离提交，不能拉取。";
        if (string.IsNullOrEmpty(upstream)) return "当前分支未设置上游，不能拉取。";
        if (tracked + untracked > 0) return $"工作区有 {tracked + untracked} 项本地改动，拉取已暂停。";
        if (ahead > 0 && behind > 0) return $"本地与上游已分叉（领先 {ahead}，落后 {behind}），不能拉取。";
        if (ahead > 0) return $"本地领先上游 {ahead} 个提交，不能拉取。";
        if (behind > 0) return $"落后上游 {behind} 个提交，可以拉取。";
        return "工作区干净，已与上游同步。";
    }

    private static GitOperationResult Failure(GitRepositoryStatus status, string message) => new() { Success = false, Message = message, Status = status };

    private static string GitError(string prefix, GitCommandResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
        detail = detail.Trim();
        return string.IsNullOrEmpty(detail) ? prefix : $"{prefix}：{detail}";
    }

    private static GitCommandResult RunGit(string directory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) return new GitCommandResult(false, false, "", "无法启动 Git 命令。");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(CommandTimeoutMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                return new GitCommandResult(true, false, stdoutTask.GetAwaiter().GetResult(), "Git 命令执行超时。");
            }
            Task.WaitAll(stdoutTask, stderrTask);
            return new GitCommandResult(true, process.ExitCode == 0, stdoutTask.Result, stderrTask.Result);
        }
        catch (Win32Exception)
        {
            return new GitCommandResult(false, false, "", "未找到 Git 命令。");
        }
        catch (Exception error)
        {
            return new GitCommandResult(false, false, "", error.Message);
        }
    }

    private sealed record GitCommandResult(bool Started, bool Success, string StdOut, string StdErr)
    {
        public static readonly GitCommandResult EmptySuccess = new(true, true, "", "");
    }
}
