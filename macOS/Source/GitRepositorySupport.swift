import Foundation

struct GitChange: Codable {
    let path: String
    let kind: String
    let status: String
}

struct GitRepositoryStatus: Codable {
    let isRepository: Bool
    let isGitAvailable: Bool
    let repositoryRoot: String
    let remoteName: String
    let remoteUrl: String
    let branch: String
    let upstream: String
    let isDetached: Bool
    let hasConflicts: Bool
    let ahead: Int
    let behind: Int
    let trackedChangeCount: Int
    let untrackedCount: Int
    let canPull: Bool
    let canClean: Bool
    let state: String
    let message: String
    let changes: [GitChange]
}

struct GitCleanPreview: Codable {
    let status: GitRepositoryStatus
    let untrackedPaths: [String]
    let message: String
}

struct GitOperationResult: Codable {
    let success: Bool
    let message: String
    let status: GitRepositoryStatus
}

struct GitOperationResponse: Codable {
    let operation: String
    let success: Bool
    let message: String
    let status: GitRepositoryStatus
}

private struct GitCommandResult {
    let started: Bool
    let success: Bool
    let stdout: String
    let stderr: String

    static let emptySuccess = GitCommandResult(started: true, success: true, stdout: "", stderr: "")
}

final class GitRepositoryService {
    func inspect(directory: URL) -> GitRepositoryStatus {
        let rootResult = runGit(in: directory, arguments: ["rev-parse", "--show-toplevel"])
        guard rootResult.started else {
            return unavailableStatus
        }
        guard rootResult.success else {
            return inactiveStatus
        }
        let root = rootResult.stdout.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !root.isEmpty else { return inactiveStatus }

        let rootURL = URL(fileURLWithPath: root, isDirectory: true)
        let branchResult = runGit(in: rootURL, arguments: ["branch", "--show-current"])
        let branch = branchResult.success ? branchResult.stdout.trimmingCharacters(in: .whitespacesAndNewlines) : ""
        let isDetached = branch.isEmpty
        let upstreamResult = isDetached
            ? GitCommandResult.emptySuccess
            : runGit(in: rootURL, arguments: ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}"])
        let upstream = upstreamResult.success ? upstreamResult.stdout.trimmingCharacters(in: .whitespacesAndNewlines) : ""

        var remoteName = ""
        if !isDetached {
            let remoteResult = runGit(in: rootURL, arguments: ["config", "--get", "branch.\(branch).remote"])
            if remoteResult.success {
                remoteName = remoteResult.stdout.trimmingCharacters(in: .whitespacesAndNewlines)
            }
        }
        var remoteUrl = ""
        if !remoteName.isEmpty {
            let remoteResult = runGit(in: rootURL, arguments: ["remote", "get-url", remoteName])
            if remoteResult.success {
                remoteUrl = remoteResult.stdout.trimmingCharacters(in: .whitespacesAndNewlines)
            }
        }

        let porcelainResult = runGit(in: rootURL, arguments: ["status", "--porcelain=v1", "--untracked-files=all"])
        guard porcelainResult.success else {
            return GitRepositoryStatus(
                isRepository: true,
                isGitAvailable: true,
                repositoryRoot: root,
                remoteName: remoteName,
                remoteUrl: remoteUrl,
                branch: branch,
                upstream: upstream,
                isDetached: isDetached,
                hasConflicts: false,
                ahead: 0,
                behind: 0,
                trackedChangeCount: 0,
                untrackedCount: 0,
                canPull: false,
                canClean: false,
                state: "error",
                message: gitError(prefix: "无法读取 Git 工作区状态", result: porcelainResult),
                changes: []
            )
        }

        let changes = parseChanges(porcelainResult.stdout)
        let trackedCount = changes.filter { $0.kind != "untracked" }.count
        let untrackedCount = changes.filter { $0.kind == "untracked" }.count
        let hasConflicts = changes.contains { isConflictStatus($0.status) }
        var ahead = 0
        var behind = 0
        if !upstream.isEmpty {
            let distanceResult = runGit(in: rootURL, arguments: ["rev-list", "--left-right", "--count", "HEAD...@{u}"])
            if distanceResult.success {
                let counts = distanceResult.stdout.split(whereSeparator: { $0.isWhitespace })
                if counts.count == 2 {
                    ahead = Int(counts[0]) ?? 0
                    behind = Int(counts[1]) ?? 0
                }
            }
        }

        let canPull = !isDetached && !upstream.isEmpty && !hasConflicts && trackedCount == 0 && untrackedCount == 0 && ahead == 0
        return GitRepositoryStatus(
            isRepository: true,
            isGitAvailable: true,
            repositoryRoot: root,
            remoteName: remoteName,
            remoteUrl: remoteUrl,
            branch: branch,
            upstream: upstream,
            isDetached: isDetached,
            hasConflicts: hasConflicts,
            ahead: ahead,
            behind: behind,
            trackedChangeCount: trackedCount,
            untrackedCount: untrackedCount,
            canPull: canPull,
            canClean: !hasConflicts,
            state: stateFor(
                isDetached: isDetached,
                upstream: upstream,
                hasConflicts: hasConflicts,
                trackedCount: trackedCount,
                untrackedCount: untrackedCount,
                ahead: ahead,
                behind: behind
            ),
            message: messageFor(
                isDetached: isDetached,
                upstream: upstream,
                hasConflicts: hasConflicts,
                trackedCount: trackedCount,
                untrackedCount: untrackedCount,
                ahead: ahead,
                behind: behind
            ),
            changes: changes
        )
    }

    func previewClean(directory: URL) -> GitCleanPreview {
        let status = inspect(directory: directory)
        guard status.isRepository, status.canClean else {
            return GitCleanPreview(status: status, untrackedPaths: [], message: status.message)
        }
        let previewResult = runGit(in: URL(fileURLWithPath: status.repositoryRoot, isDirectory: true), arguments: ["clean", "-nd"])
        guard previewResult.success else {
            return GitCleanPreview(
                status: status,
                untrackedPaths: [],
                message: gitError(prefix: "无法预览未跟踪文件", result: previewResult)
            )
        }
        let paths = previewResult.stdout.split(whereSeparator: \.isNewline)
            .map(String.init)
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { $0.hasPrefix("Would remove ") }
            .map { String($0.dropFirst("Would remove ".count)) }
        return GitCleanPreview(status: status, untrackedPaths: paths, message: "")
    }

    func pull(directory: URL) -> GitOperationResult {
        let initial = inspect(directory: directory)
        guard initial.isRepository else { return failure(status: initial, message: initial.message) }

        let pullResult = runGit(in: URL(fileURLWithPath: initial.repositoryRoot, isDirectory: true), arguments: ["pull", "--ff-only"])
        guard pullResult.success else {
            return failure(status: inspect(directory: directory), message: gitError(prefix: "拉取失败", result: pullResult))
        }
        let message = initial.behind > 0
            ? "已快进拉取 \(initial.behind) 个提交，正在刷新配置。"
            : "代码已是最新，正在刷新配置。"
        return GitOperationResult(success: true, message: message, status: inspect(directory: directory))
    }

    func clean(directory: URL, trackedPaths: [String], untrackedPaths: [String]) -> GitOperationResult {
        let before = inspect(directory: directory)
        guard before.isRepository, before.canClean else { return failure(status: before, message: before.message) }

        let rootURL = URL(fileURLWithPath: before.repositoryRoot, isDirectory: true)
        let selectedTracked = selectAllowedPaths(
            trackedPaths,
            allowed: before.changes.filter { $0.kind == "tracked" }.map(\.path)
        )
        let untrackedPreview = previewClean(directory: directory)
        guard untrackedPreview.message.isEmpty else {
            return failure(status: untrackedPreview.status, message: untrackedPreview.message)
        }
        let selectedUntracked = selectAllowedPaths(untrackedPaths, allowed: untrackedPreview.untrackedPaths)
        guard !selectedTracked.isEmpty || !selectedUntracked.isEmpty else {
            return failure(status: before, message: "请至少选择一个要清理的文件或目录。")
        }

        if !selectedTracked.isEmpty {
            let restoreResult = runGit(in: rootURL, arguments: ["restore", "--source=HEAD", "--staged", "--worktree", "--"] + selectedTracked)
            guard restoreResult.success else {
                return failure(status: inspect(directory: directory), message: gitError(prefix: "恢复已跟踪文件失败", result: restoreResult))
            }
        }

        if !selectedUntracked.isEmpty {
            let cleanResult = runGit(in: rootURL, arguments: ["clean", "-df", "--"] + selectedUntracked)
            guard cleanResult.success else {
                return failure(status: inspect(directory: directory), message: gitError(prefix: "删除未跟踪文件失败", result: cleanResult))
            }
        }

        var parts: [String] = []
        if !selectedTracked.isEmpty { parts.append("已恢复 \(selectedTracked.count) 项已跟踪改动") }
        if !selectedUntracked.isEmpty { parts.append("已删除 \(selectedUntracked.count) 项未跟踪文件或目录") }
        let message = parts.joined(separator: "，") + "；Git 忽略文件未删除。"
        return GitOperationResult(success: true, message: message, status: inspect(directory: directory))
    }

    private func selectAllowedPaths(_ selectedPaths: [String], allowed allowedPaths: [String]) -> [String] {
        let allowed = Set(allowedPaths)
        var seen = Set<String>()
        return selectedPaths.filter { path in
            !path.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && allowed.contains(path) && seen.insert(path).inserted
        }
    }

    private var unavailableStatus: GitRepositoryStatus {
        GitRepositoryStatus(
            isRepository: false,
            isGitAvailable: false,
            repositoryRoot: "",
            remoteName: "",
            remoteUrl: "",
            branch: "",
            upstream: "",
            isDetached: false,
            hasConflicts: false,
            ahead: 0,
            behind: 0,
            trackedChangeCount: 0,
            untrackedCount: 0,
            canPull: false,
            canClean: false,
            state: "error",
            message: "未找到 Git 命令。请安装 Xcode Command Line Tools 后重试。",
            changes: []
        )
    }

    private var inactiveStatus: GitRepositoryStatus {
        GitRepositoryStatus(
            isRepository: false,
            isGitAvailable: true,
            repositoryRoot: "",
            remoteName: "",
            remoteUrl: "",
            branch: "",
            upstream: "",
            isDetached: false,
            hasConflicts: false,
            ahead: 0,
            behind: 0,
            trackedChangeCount: 0,
            untrackedCount: 0,
            canPull: false,
            canClean: false,
            state: "inactive",
            message: "当前配置目录不属于 Git 仓库。",
            changes: []
        )
    }

    private func parseChanges(_ output: String) -> [GitChange] {
        output.split(whereSeparator: \.isNewline).compactMap { line in
            let text = String(line)
            guard text.count >= 3 else { return nil }
            let status = String(text.prefix(2))
            let path = String(text.dropFirst(3)).trimmingCharacters(in: .whitespacesAndNewlines)
            return GitChange(
                path: path,
                kind: status == "??" ? "untracked" : (isConflictStatus(status) ? "conflict" : "tracked"),
                status: status
            )
        }
    }

    private func isConflictStatus(_ status: String) -> Bool {
        status.contains("U") || status == "AA" || status == "DD"
    }

    private func stateFor(isDetached: Bool, upstream: String, hasConflicts: Bool, trackedCount: Int, untrackedCount: Int, ahead: Int, behind: Int) -> String {
        if hasConflicts || isDetached || upstream.isEmpty || trackedCount + untrackedCount > 0 || ahead > 0 { return "warning" }
        return behind > 0 ? "ready" : "synced"
    }

    private func messageFor(isDetached: Bool, upstream: String, hasConflicts: Bool, trackedCount: Int, untrackedCount: Int, ahead: Int, behind: Int) -> String {
        if hasConflicts { return "存在未解决冲突，请先在 Git 工具中处理。" }
        if isDetached { return "当前处于游离提交，不能拉取。" }
        if upstream.isEmpty { return "当前分支未设置上游，不能拉取。" }
        if trackedCount + untrackedCount > 0 { return "工作区有 \(trackedCount + untrackedCount) 项本地改动，拉取已暂停。" }
        if ahead > 0 && behind > 0 { return "本地与上游已分叉（领先 \(ahead)，落后 \(behind)），不能拉取。" }
        if ahead > 0 { return "本地领先上游 \(ahead) 个提交，不能拉取。" }
        if behind > 0 { return "落后上游 \(behind) 个提交，可以拉取。" }
        return "工作区干净，已与上游同步。"
    }

    private func failure(status: GitRepositoryStatus, message: String) -> GitOperationResult {
        GitOperationResult(success: false, message: message, status: status)
    }

    private func gitError(prefix: String, result: GitCommandResult) -> String {
        let detail = (result.stderr.isEmpty ? result.stdout : result.stderr).trimmingCharacters(in: .whitespacesAndNewlines)
        return detail.isEmpty ? prefix : "\(prefix)：\(detail)"
    }

    private func runGit(in directory: URL, arguments: [String]) -> GitCommandResult {
        let executablePath = "/usr/bin/git"
        guard FileManager.default.isExecutableFile(atPath: executablePath) else {
            return GitCommandResult(started: false, success: false, stdout: "", stderr: "未找到 Git 命令。")
        }
        let process = Process()
        let stdout = Pipe()
        let stderr = Pipe()
        process.executableURL = URL(fileURLWithPath: executablePath)
        process.currentDirectoryURL = directory
        process.arguments = arguments
        process.standardOutput = stdout
        process.standardError = stderr
        do {
            try process.run()
            process.waitUntilExit()
            return GitCommandResult(
                started: true,
                success: process.terminationStatus == 0,
                stdout: String(data: stdout.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? "",
                stderr: String(data: stderr.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
            )
        } catch {
            return GitCommandResult(started: false, success: false, stdout: "", stderr: error.localizedDescription)
        }
    }
}
