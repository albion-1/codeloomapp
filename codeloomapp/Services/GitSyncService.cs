using System.Diagnostics;
using System.IO;

namespace codeloomapp.Services;

public sealed class GitSyncService
{
    public bool IsGitRepository(string path)
    {
        if (!Directory.Exists(path))
            return false;

        var gitMarker = Path.Combine(path, ".git");
        return Directory.Exists(gitMarker) || File.Exists(gitMarker);
    }

    public async Task<GitRepositoryStatus> GetStatusAsync(string repositoryPath, bool fetchRemote = false)
    {
        if (!IsGitRepository(repositoryPath))
            return GitRepositoryStatus.Unavailable("The selected folder is not a Git repository.");

        var warning = string.Empty;
        if (fetchRemote)
        {
            var fetch = await RunGitAsync(repositoryPath, "fetch", "--prune");
            if (!fetch.Success)
                warning = "Remote refresh failed: " + fetch.Output;
        }

        var porcelain = await RunGitAsync(repositoryPath, "status", "--porcelain=v1");
        if (!porcelain.Success)
            return GitRepositoryStatus.Unavailable("Could not inspect Git status: " + porcelain.Output);

        var branchResult = await RunGitAsync(repositoryPath, "symbolic-ref", "--quiet", "--short", "HEAD");
        var branch = branchResult.Success && !string.IsNullOrWhiteSpace(branchResult.Output)
            ? branchResult.Output.Trim()
            : "detached HEAD";

        var upstreamResult = await RunGitAsync(
            repositoryPath,
            "rev-parse",
            "--abbrev-ref",
            "--symbolic-full-name",
            "@{upstream}");
        var upstream = upstreamResult.Success ? upstreamResult.Output.Trim() : string.Empty;

        var remoteResult = await RunGitAsync(repositoryPath, "remote", "get-url", "origin");
        var remoteUrl = remoteResult.Success ? remoteResult.Output.Trim() : string.Empty;

        var ahead = 0;
        var behind = 0;
        if (!string.IsNullOrWhiteSpace(upstream))
        {
            var counts = await RunGitAsync(
                repositoryPath,
                "rev-list",
                "--left-right",
                "--count",
                $"HEAD...{upstream}");

            if (counts.Success)
            {
                var parts = counts.Output.Split(
                    new[] { ' ', '\t', '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    _ = int.TryParse(parts[0], out ahead);
                    _ = int.TryParse(parts[1], out behind);
                }
            }
        }

        var changes = porcelain.Output
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseStatusLine)
            .Where(change => change is not null)
            .Cast<GitChangedFile>()
            .ToList();

        var (operationInProgress, operationName) = await DetectOperationAsync(repositoryPath);

        return new GitRepositoryStatus(
            true,
            branch,
            upstream,
            remoteUrl,
            ahead,
            behind,
            operationInProgress,
            operationName,
            changes,
            warning);
    }

    public async Task<GitSyncResult> SyncAsync(string repositoryPath)
    {
        if (!IsGitRepository(repositoryPath))
            return GitSyncResult.Fail("The selected folder is not a Git repository.");

        var before = await GetStatusAsync(repositoryPath);
        if (!before.Available)
            return GitSyncResult.Fail(before.WarningMessage);

        if (before.OperationInProgress || before.ConflictCount > 0)
        {
            return GitSyncResult.Fail(
                "Git has an unfinished operation or conflict. Open Git details before syncing again.");
        }

        var unrelatedChanges = before.Changes.Where(change => !change.IsCodeLoom).ToList();
        if (unrelatedChanges.Count > 0)
        {
            var shown = string.Join("\n", unrelatedChanges.Take(6).Select(change => "• " + change.Path));
            var extra = unrelatedChanges.Count > 6
                ? $"\n• …and {unrelatedChanges.Count - 6} more"
                : string.Empty;

            return GitSyncResult.Fail(
                "Code Loom found local changes outside .codeloom and left them untouched. " +
                "Commit, stash, or discard those changes in Visual Studio/Git before syncing.\n\n" +
                shown + extra);
        }

        // Only Code Loom metadata is staged automatically. Unrelated repository files
        // are deliberately excluded from Code Loom's commit.
        var add = await RunGitAsync(repositoryPath, "add", "-A", "--", ".codeloom");
        if (!add.Success)
            return GitSyncResult.Fail("Could not stage Code Loom project changes:\n" + add.Output);

        var staged = await RunGitAsync(repositoryPath, "diff", "--cached", "--quiet", "--", ".codeloom");
        if (staged.ExitCode is not (0 or 1))
            return GitSyncResult.Fail("Could not inspect staged Code Loom changes:\n" + staged.Output);

        var committed = false;
        if (staged.ExitCode == 1)
        {
            var message = $"Code Loom sync {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            var commit = await RunGitAsync(repositoryPath, "commit", "-m", message);
            if (!commit.Success)
                return GitSyncResult.Fail("Commit failed:\n" + commit.Output);

            committed = true;
        }

        var fetch = await RunGitAsync(repositoryPath, "fetch", "--prune");
        if (!fetch.Success)
            return GitSyncResult.Fail("Could not refresh GitHub before syncing:\n" + fetch.Output);

        var refreshed = await GetStatusAsync(repositoryPath);
        if (!refreshed.Available)
            return GitSyncResult.Fail(refreshed.WarningMessage);

        if (string.Equals(refreshed.Branch, "detached HEAD", StringComparison.OrdinalIgnoreCase))
            return GitSyncResult.Fail("This repository is in detached HEAD state. Check out a branch before syncing.");

        if (string.IsNullOrWhiteSpace(refreshed.Upstream))
        {
            if (string.IsNullOrWhiteSpace(refreshed.RemoteUrl))
                return GitSyncResult.Fail("This repository has no upstream branch or origin remote to push to.");

            var publish = await RunGitAsync(repositoryPath, "push", "-u", "origin", "HEAD");
            return publish.Success
                ? GitSyncResult.Ok(committed
                    ? "Committed Code Loom changes and published the branch to GitHub."
                    : "Published the branch and confirmed GitHub is up to date.")
                : GitSyncResult.Fail("Push failed:\n" + publish.Output);
        }

        if (refreshed.Behind > 0)
        {
            var rebase = await RunGitAsync(repositoryPath, "rebase", refreshed.Upstream);
            if (!rebase.Success)
            {
                var conflictStatus = await GetStatusAsync(repositoryPath);
                if (conflictStatus.ConflictCount > 0)
                {
                    return GitSyncResult.Fail(
                        $"Sync paused because Git found {conflictStatus.ConflictCount} conflict(s). " +
                        "Open Git details to see the files. Resolve and stage them in Visual Studio, then use Continue Rebase.");
                }

                return GitSyncResult.Fail("Rebase failed. Code Loom did not push anything.\n" + rebase.Output);
            }
        }

        var push = await RunGitAsync(repositoryPath, "push");
        if (!push.Success)
            return GitSyncResult.Fail("Push failed:\n" + push.Output);

        return GitSyncResult.Ok(committed
            ? "Committed Code Loom changes, incorporated GitHub updates, and pushed successfully."
            : "GitHub updates were incorporated and the repository is up to date.");
    }

    public async Task<GitSyncResult> ContinueRebaseAsync(string repositoryPath)
    {
        var status = await GetStatusAsync(repositoryPath);
        if (!status.Available)
            return GitSyncResult.Fail(status.WarningMessage);

        if (!status.OperationInProgress || !string.Equals(status.OperationName, "rebase", StringComparison.Ordinal))
            return GitSyncResult.Fail("There is no Code Loom sync rebase waiting to continue.");

        if (status.ConflictCount > 0)
            return GitSyncResult.Fail("Resolve and stage every conflicted file before continuing the rebase.");

        var continuation = await RunGitAsync(
            repositoryPath,
            "-c",
            "core.editor=true",
            "rebase",
            "--continue");

        if (!continuation.Success)
        {
            var afterFailure = await GetStatusAsync(repositoryPath);
            return afterFailure.ConflictCount > 0
                ? GitSyncResult.Fail(
                    $"The rebase reached another conflict ({afterFailure.ConflictCount} file(s)). Resolve it and continue again.")
                : GitSyncResult.Fail("Could not continue the rebase:\n" + continuation.Output);
        }

        var after = await GetStatusAsync(repositoryPath);
        if (after.OperationInProgress)
            return GitSyncResult.Ok("The rebase advanced and still has another step to finish.");

        var push = await RunGitAsync(repositoryPath, "push");
        return push.Success
            ? GitSyncResult.Ok("Rebase completed and changes were pushed to GitHub.")
            : GitSyncResult.Fail("The rebase completed, but the push failed:\n" + push.Output);
    }

    private async Task<(bool InProgress, string Name)> DetectOperationAsync(string repositoryPath)
    {
        var gitDirResult = await RunGitAsync(repositoryPath, "rev-parse", "--git-dir");
        if (!gitDirResult.Success || string.IsNullOrWhiteSpace(gitDirResult.Output))
            return (false, string.Empty);

        var gitDirectory = gitDirResult.Output.Trim();
        if (!Path.IsPathRooted(gitDirectory))
            gitDirectory = Path.GetFullPath(Path.Combine(repositoryPath, gitDirectory));

        if (Directory.Exists(Path.Combine(gitDirectory, "rebase-merge"))
            || Directory.Exists(Path.Combine(gitDirectory, "rebase-apply")))
        {
            return (true, "rebase");
        }

        if (File.Exists(Path.Combine(gitDirectory, "MERGE_HEAD")))
            return (true, "merge");

        if (File.Exists(Path.Combine(gitDirectory, "CHERRY_PICK_HEAD")))
            return (true, "cherry-pick");

        return (false, string.Empty);
    }

    private static GitChangedFile? ParseStatusLine(string line)
    {
        if (line.Length < 3)
            return null;

        var status = line[..2];
        var path = line.Length > 3 ? line[3..].Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var renameSeparator = path.LastIndexOf(" -> ", StringComparison.Ordinal);
        if (renameSeparator >= 0)
            path = path[(renameSeparator + 4)..];

        path = path.Trim('"');
        var normalized = path.Replace('\\', '/').TrimStart('/');
        var isCodeLoom = normalized.StartsWith(".codeloom/", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(normalized, ".codeloom", StringComparison.OrdinalIgnoreCase);
        var isConflict = status is "DD" or "AU" or "UD" or "UA" or "DU" or "AA" or "UU"
                         || status.Contains('U');

        return new GitChangedFile(status, path, isCodeLoom, isConflict);
    }

    private static async Task<GitCommandResult> RunGitAsync(string workingDirectory, params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            var output = (await outputTask).Trim();
            var error = (await errorTask).Trim();
            var combined = string.Join(
                Environment.NewLine,
                new[] { output, error }.Where(text => !string.IsNullOrWhiteSpace(text)));

            return new GitCommandResult(process.ExitCode, combined);
        }
        catch (Exception exception)
        {
            return new GitCommandResult(
                -1,
                "Could not run Git. Make sure Git is installed and available in PATH.\n" + exception.Message);
        }
    }

    private sealed record GitCommandResult(int ExitCode, string Output)
    {
        public bool Success => ExitCode == 0;
    }
}

public sealed record GitChangedFile(string Status, string Path, bool IsCodeLoom, bool IsConflict);

public sealed record GitRepositoryStatus(
    bool Available,
    string Branch,
    string Upstream,
    string RemoteUrl,
    int Ahead,
    int Behind,
    bool OperationInProgress,
    string OperationName,
    IReadOnlyList<GitChangedFile> Changes,
    string WarningMessage)
{
    public int ConflictCount => Changes.Count(change => change.IsConflict);
    public int CodeLoomChangeCount => Changes.Count(change => change.IsCodeLoom);
    public int OtherChangeCount => Changes.Count(change => !change.IsCodeLoom);

    public string CompactSummary
    {
        get
        {
            if (!Available)
                return "Git: unavailable";

            var pieces = new List<string> { Branch };
            if (ConflictCount > 0)
                pieces.Add(ConflictCount == 1 ? "1 conflict" : $"{ConflictCount} conflicts");
            else if (OperationInProgress)
                pieces.Add(OperationName + " in progress");
            else if (Changes.Count == 0)
                pieces.Add("clean");
            else
                pieces.Add(Changes.Count == 1 ? "1 local change" : $"{Changes.Count} local changes");

            if (Ahead > 0)
                pieces.Add($"↑{Ahead}");
            if (Behind > 0)
                pieces.Add($"↓{Behind}");
            if (string.IsNullOrWhiteSpace(Upstream))
                pieces.Add("no upstream");

            return "Git: " + string.Join(" · ", pieces);
        }
    }

    public static GitRepositoryStatus Unavailable(string message)
    {
        return new GitRepositoryStatus(
            false,
            string.Empty,
            string.Empty,
            string.Empty,
            0,
            0,
            false,
            string.Empty,
            Array.Empty<GitChangedFile>(),
            message);
    }
}

public sealed record GitSyncResult(bool Success, string Message)
{
    public static GitSyncResult Ok(string message) => new(true, message);
    public static GitSyncResult Fail(string message) => new(false, message);
}
