using System.Diagnostics;

namespace codeloomapp.Services;

public sealed class RepositoryGitSyncService
{
    public async Task<GitSyncResult> SyncAsync(string repositoryPath)
    {
        // A 0.1.7 Pull could leave only .codeloom/project.json in an unresolved
        // stash-pop conflict. That file is generated metadata, not source. Repair that
        // exact Code Loom-owned state before Push; real source conflicts still stop.
        var repair = await RepositoryGitPullService.RepairCodeLoomMetadataConflictAsync(repositoryPath);
        if (!repair.Success)
            return repair;

        var status = await RunGitAsync(repositoryPath, "status", "--porcelain=v1", "--untracked-files=all");
        if (!status.Success)
            return GitSyncResult.Fail("Could not inspect Git status:\n" + status.Output);

        var changedPaths = ParseChangedPaths(status.Output)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var managed = changedPaths.Where(IsManagedPath).ToList();
        var unrelated = changedPaths.Where(path => !IsManagedPath(path)).ToList();

        // Push only stages source Code Loom actually owns. Other repository files are
        // allowed to remain modified locally; Code Loom no longer demands that the user
        // delete, stash, or commit them just to push C# work.
        foreach (var path in managed)
        {
            var add = await RunGitAsync(repositoryPath, "add", "--all", "--", path);
            if (!add.Success)
                return GitSyncResult.Fail($"Could not stage {path}:\n" + add.Output);
        }

        var staged = await RunGitAsync(repositoryPath, "diff", "--cached", "--quiet");
        if (staged.ExitCode is not (0 or 1))
            return GitSyncResult.Fail("Could not inspect staged Code Loom changes:\n" + staged.Output);

        var committed = false;
        if (staged.ExitCode == 1)
        {
            var commit = await RunGitAsync(
                repositoryPath,
                "commit",
                "-m",
                $"Code Loom sync {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            if (!commit.Success)
                return GitSyncResult.Fail("Commit failed:\n" + commit.Output);
            committed = true;
        }

        var fetch = await RunGitAsync(repositoryPath, "fetch", "--prune");
        if (!fetch.Success)
            return GitSyncResult.Fail("Could not refresh GitHub before pushing:\n" + fetch.Output);

        var branch = await RunGitAsync(repositoryPath, "symbolic-ref", "--quiet", "--short", "HEAD");
        if (!branch.Success || string.IsNullOrWhiteSpace(branch.Output))
            return GitSyncResult.Fail("Check out a branch before pushing.");

        var upstream = await RunGitAsync(
            repositoryPath,
            "rev-parse",
            "--abbrev-ref",
            "--symbolic-full-name",
            "@{upstream}");
        if (!upstream.Success || string.IsNullOrWhiteSpace(upstream.Output))
        {
            var remote = await RunGitAsync(repositoryPath, "remote", "get-url", "origin");
            if (!remote.Success || string.IsNullOrWhiteSpace(remote.Output))
                return GitSyncResult.Fail("This repository has no upstream branch or origin remote to push to.");

            if (!committed)
            {
                var aheadWithoutUpstream = await RunGitAsync(repositoryPath, "rev-list", "--count", "HEAD");
                if (!aheadWithoutUpstream.Success)
                    return GitSyncResult.Fail("Could not inspect local commits before publishing the branch.\n" + aheadWithoutUpstream.Output);
            }

            var publish = await RunGitAsync(repositoryPath, "push", "-u", "origin", "HEAD");
            return publish.Success
                ? GitSyncResult.Ok(BuildSuccessMessage(
                    committed ? "Committed Code Loom C# source and published the branch to GitHub." : "Published the branch to GitHub.",
                    unrelated.Count))
                : GitSyncResult.Fail("Push failed:\n" + publish.Output);
        }

        var counts = await RunGitAsync(
            repositoryPath,
            "rev-list",
            "--left-right",
            "--count",
            $"HEAD...{upstream.Output.Trim()}");
        if (!counts.Success)
            return GitSyncResult.Fail("Could not compare the local branch with GitHub:\n" + counts.Output);

        ParseAheadBehind(counts.Output, out var ahead, out var behind);

        if (behind > 0)
        {
            if (unrelated.Count > 0)
            {
                var shown = string.Join("\n", unrelated.Take(8).Select(path => "• " + path));
                return GitSyncResult.Fail(
                    "GitHub has incoming changes, so Code Loom stopped before rebasing while unrelated local files are present. " +
                    "Nothing needs to be deleted. Use Pull first; Pull temporarily protects local working files and restores them afterward.\n\n" +
                    $"Unrelated local files left untouched ({unrelated.Count}):\n" + shown);
            }

            var rebase = await RunGitAsync(repositoryPath, "rebase", upstream.Output.Trim());
            if (!rebase.Success)
            {
                return GitSyncResult.Fail(
                    "Push paused because local and GitHub commits could not be reconciled automatically. " +
                    "No unrelated files were deleted. Resolve the Git conflict before pushing again.\n\n" + rebase.Output);
            }

            var refreshedCounts = await RunGitAsync(
                repositoryPath,
                "rev-list",
                "--left-right",
                "--count",
                $"HEAD...{upstream.Output.Trim()}");
            if (refreshedCounts.Success)
                ParseAheadBehind(refreshedCounts.Output, out ahead, out _);
        }

        if (ahead <= 0 && !committed)
        {
            return GitSyncResult.Ok(BuildSuccessMessage(
                "Nothing to push — Code Loom source is already up to date with GitHub.",
                unrelated.Count));
        }

        var push = await RunGitAsync(repositoryPath, "push");
        if (!push.Success)
            return GitSyncResult.Fail("Push failed:\n" + push.Output);

        return GitSyncResult.Ok(BuildSuccessMessage(
            committed
                ? "Committed Code Loom C# source and pushed successfully."
                : "Pushed existing local commits successfully.",
            unrelated.Count));
    }

    private static string BuildSuccessMessage(string message, int unrelatedCount)
    {
        if (unrelatedCount <= 0)
            return message;

        return message +
               $" {unrelatedCount} unrelated local file(s) were left exactly as they were and were not included in the Code Loom commit.";
    }

    private static void ParseAheadBehind(string output, out int ahead, out int behind)
    {
        ahead = 0;
        behind = 0;
        var parts = output.Split(
            new[] { ' ', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return;

        _ = int.TryParse(parts[0], out ahead);
        _ = int.TryParse(parts[1], out behind);
    }

    private static IEnumerable<string> ParseChangedPaths(string output)
    {
        foreach (var line in output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 4)
                continue;

            var path = line[3..].Trim().Trim('"');
            var renameSeparator = path.LastIndexOf(" -> ", StringComparison.Ordinal);
            if (renameSeparator >= 0)
                path = path[(renameSeparator + 4)..];

            var normalized = path.Replace('\\', '/').TrimStart('/');
            if (!string.IsNullOrWhiteSpace(normalized))
                yield return normalized;
        }
    }

    private static bool IsManagedPath(string normalizedPath)
    {
        if (IsLocalOnlyCodeLoomBackup(normalizedPath))
            return false;

        return normalizedPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
               || normalizedPath.EndsWith(".cs.meta", StringComparison.OrdinalIgnoreCase)
               || normalizedPath.StartsWith(".codeloom/", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalizedPath, ".codeloom", StringComparison.OrdinalIgnoreCase)
               || normalizedPath.StartsWith(UnityExportService.GeneratedRelativePath + "/", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalizedPath, UnityExportService.GeneratedRelativePath, StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalizedPath, UnityExportService.GeneratedRelativePath + ".meta", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalizedPath, "Assets/CodeLoom.meta", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLocalOnlyCodeLoomBackup(string normalizedPath)
    {
        return string.Equals(
                   normalizedPath,
                   ".codeloom/project.legacy-backup.json",
                   StringComparison.OrdinalIgnoreCase)
               || normalizedPath.StartsWith(
                   ".codeloom/project.pull-safety-backup",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<GitCommandResult> RunGitAsync(string workingDirectory, params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = GitExecutableLocator.Resolve(),
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

            // Porcelain status lines deliberately begin with spaces for some states.
            // TrimEnd preserves those two status columns instead of corrupting the first path.
            var output = (await outputTask).TrimEnd();
            var error = (await errorTask).Trim();
            return new GitCommandResult(
                process.ExitCode,
                string.Join(Environment.NewLine, new[] { output, error }.Where(value => !string.IsNullOrWhiteSpace(value))));
        }
        catch (Exception exception)
        {
            return new GitCommandResult(
                -1,
                "Could not run Git using " + GitExecutableLocator.DescribeResolvedPath() + ".\n" + exception.Message);
        }
    }

    private sealed record GitCommandResult(int ExitCode, string Output)
    {
        public bool Success => ExitCode == 0;
    }
}
