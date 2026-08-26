using System.Diagnostics;

namespace codeloomapp.Services;

public sealed class RepositoryGitPullService
{
    private const string ProjectMetadataPath = ".codeloom/project.json";
    private const string SafetyStashMessage = "Code Loom automatic pull safety";

    public async Task<GitSyncResult> PullAsync(string repositoryPath)
    {
        // 0.1.7 could leave project.json conflicted when a safety stash was restored
        // after GitHub had also changed Code Loom metadata. project.json is generated
        // metadata, not C# source, so repair that exact interrupted state first. Any
        // conflict involving real source or another repository file still stops here.
        var repair = await RepairCodeLoomMetadataConflictAsync(repositoryPath);
        if (!repair.Success)
            return repair;

        // Code Loom rewrites project.json as its local metadata index. It should never
        // be part of the Git safety stash because the remote version can legitimately
        // change whenever another Code Loom/ChatGPT session reorganizes source files.
        // The MainWindow keeps the semantic metadata in memory/recovery and merges it
        // back after Pull, so the physical project.json can safely return to HEAD here.
        var metadataReset = await ResetWorkingMetadataToHeadAsync(repositoryPath);
        if (!metadataReset.Success)
            return metadataReset;

        var initial = await RunGitAsync(
            repositoryPath,
            "status",
            "--porcelain=v1",
            "--untracked-files=all");
        if (!initial.Success)
            return GitSyncResult.Fail("Could not inspect the local repository before Pull:\n" + initial.Output);

        var hadLocalChanges = !string.IsNullOrWhiteSpace(initial.StdOut);
        var stashCreated = false;

        if (hadLocalChanges)
        {
            var stash = await RunGitAsync(
                repositoryPath,
                "stash",
                "push",
                "--include-untracked",
                "--message",
                SafetyStashMessage);
            if (!stash.Success)
            {
                return GitSyncResult.Fail(
                    "Code Loom found local working files, but Git could not temporarily protect them before Pull. " +
                    "Nothing was deleted or overwritten.\n\n" + stash.Output);
            }

            stashCreated = !stash.StdOut.Contains(
                "No local changes to save",
                StringComparison.OrdinalIgnoreCase);
        }

        var fetch = await RunGitAsync(repositoryPath, "fetch", "--prune");
        if (!fetch.Success)
            return await FailAndRestoreAsync(repositoryPath, stashCreated, "Could not refresh GitHub before Pull:\n" + fetch.Output);

        var branch = await RunGitAsync(repositoryPath, "symbolic-ref", "--quiet", "--short", "HEAD");
        if (!branch.Success || string.IsNullOrWhiteSpace(branch.StdOut))
            return await FailAndRestoreAsync(repositoryPath, stashCreated, "Check out a normal branch before Pull.");

        var upstream = await RunGitAsync(
            repositoryPath,
            "rev-parse",
            "--abbrev-ref",
            "--symbolic-full-name",
            "@{upstream}");
        if (!upstream.Success || string.IsNullOrWhiteSpace(upstream.StdOut))
        {
            return await FailAndRestoreAsync(
                repositoryPath,
                stashCreated,
                "This branch has no upstream GitHub branch to pull from.");
        }

        var upstreamName = upstream.StdOut.Trim();
        var counts = await RunGitAsync(
            repositoryPath,
            "rev-list",
            "--left-right",
            "--count",
            $"HEAD...{upstreamName}");
        if (!counts.Success)
            return await FailAndRestoreAsync(repositoryPath, stashCreated, "Could not compare the local branch with GitHub:\n" + counts.Output);

        ParseAheadBehind(counts.StdOut, out var ahead, out var behind);
        if (behind <= 0)
        {
            var restore = await RestoreSafetyStashAsync(repositoryPath, stashCreated);
            if (!restore.Success)
                return restore;

            return GitSyncResult.Ok(
                ahead > 0
                    ? "There are no incoming GitHub changes. Local commits are still waiting to be pushed."
                    : "The repository is already up to date with GitHub.");
        }

        if (ahead > 0)
        {
            var rebase = await RunGitAsync(repositoryPath, "rebase", upstreamName);
            if (!rebase.Success)
            {
                await RunGitAsync(repositoryPath, "rebase", "--abort");
                return await FailAndRestoreAsync(
                    repositoryPath,
                    stashCreated,
                    "GitHub and the local branch both contain commits that could not be reconciled automatically. " +
                    "Code Loom restored the branch to its pre-Pull state.\n\n" + rebase.Output);
            }
        }
        else
        {
            var fastForward = await RunGitAsync(
                repositoryPath,
                "merge",
                "--ff-only",
                upstreamName);
            if (!fastForward.Success)
            {
                return await FailAndRestoreAsync(
                    repositoryPath,
                    stashCreated,
                    "Could not apply the GitHub changes as a safe fast-forward.\n\n" + fastForward.Output);
            }
        }

        var restored = await RestoreSafetyStashAsync(repositoryPath, stashCreated);
        if (!restored.Success)
            return restored;

        var localNote = stashCreated
            ? " Local uncommitted files were temporarily protected and restored."
            : string.Empty;
        return GitSyncResult.Ok(
            behind == 1
                ? "Applied 1 incoming GitHub commit." + localNote
                : $"Applied {behind} incoming GitHub commits." + localNote);
    }

    public static async Task<GitSyncResult> RepairCodeLoomMetadataConflictAsync(string repositoryPath)
    {
        var unresolved = await RunGitAsync(repositoryPath, "diff", "--name-only", "--diff-filter=U");
        if (!unresolved.Success)
            return GitSyncResult.Fail("Could not inspect unresolved Git files:\n" + unresolved.Output);

        // Only stdout contains path names. Git can emit harmless line-ending warnings
        // on stderr; treating those warnings as filenames caused 0.1.7's repair path to
        // falsely report a second conflict.
        var conflicts = SplitPaths(unresolved.StdOut);
        if (conflicts.Count == 0)
            return GitSyncResult.Ok("No interrupted Code Loom metadata conflict found.");

        var nonMetadata = conflicts
            .Where(path => !string.Equals(path, ProjectMetadataPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (nonMetadata.Count > 0)
        {
            var shown = string.Join("\n", nonMetadata.Take(8).Select(path => "• " + path));
            return GitSyncResult.Fail(
                "Git has a real unresolved repository conflict, so Code Loom will not guess which source should win. " +
                "Resolve these files first:\n\n" + shown);
        }

        var restore = await RunGitAsync(
            repositoryPath,
            "restore",
            "--source=HEAD",
            "--staged",
            "--worktree",
            "--",
            ProjectMetadataPath);
        if (!restore.Success)
        {
            return GitSyncResult.Fail(
                "Code Loom recognized the leftover conflict as its own generated project metadata, but Git could not repair it automatically.\n\n" +
                restore.Output);
        }

        // A failed stash pop keeps the stash. Once project.json is restored to the
        // post-Pull HEAD and no other file is conflicted, every non-conflicting stash
        // change has already been applied to the working tree. Drop only Code Loom's
        // own top safety stash so it cannot cause another false conflict later.
        var topStash = await RunGitAsync(repositoryPath, "stash", "list", "-n", "1", "--format=%gd%x09%s");
        if (topStash.Success && topStash.StdOut.Contains(SafetyStashMessage, StringComparison.OrdinalIgnoreCase))
            _ = await RunGitAsync(repositoryPath, "stash", "drop", "stash@{0}");

        return GitSyncResult.Ok("Recovered the interrupted Code Loom metadata restore.");
    }

    private static async Task<GitSyncResult> ResetWorkingMetadataToHeadAsync(string repositoryPath)
    {
        var metadataStatus = await RunGitAsync(
            repositoryPath,
            "status",
            "--porcelain=v1",
            "--untracked-files=all",
            "--",
            ProjectMetadataPath);
        if (!metadataStatus.Success)
            return GitSyncResult.Fail("Could not inspect Code Loom project metadata before Pull:\n" + metadataStatus.Output);

        if (string.IsNullOrWhiteSpace(metadataStatus.StdOut))
            return GitSyncResult.Ok("Code Loom metadata is already clean.");

        var tracked = await RunGitAsync(repositoryPath, "ls-files", "--error-unmatch", "--", ProjectMetadataPath);
        if (tracked.Success)
        {
            var restore = await RunGitAsync(
                repositoryPath,
                "restore",
                "--source=HEAD",
                "--staged",
                "--worktree",
                "--",
                ProjectMetadataPath);
            return restore.Success
                ? GitSyncResult.Ok("Prepared generated Code Loom metadata for Pull.")
                : GitSyncResult.Fail("Could not prepare generated Code Loom metadata for Pull:\n" + restore.Output);
        }

        try
        {
            var fullPath = Path.Combine(
                repositoryPath,
                ProjectMetadataPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullPath))
                File.Delete(fullPath);
            return GitSyncResult.Ok("Prepared untracked Code Loom metadata for Pull.");
        }
        catch (Exception exception)
        {
            return GitSyncResult.Fail(
                "Could not temporarily clear untracked Code Loom metadata before Pull.\n" + exception.Message);
        }
    }

    private static async Task<GitSyncResult> FailAndRestoreAsync(
        string repositoryPath,
        bool stashCreated,
        string message)
    {
        var restore = await RestoreSafetyStashAsync(repositoryPath, stashCreated);
        if (restore.Success)
            return GitSyncResult.Fail(message);

        return GitSyncResult.Fail(
            message +
            "\n\nCode Loom also could not automatically restore the temporary Git safety stash. " +
            "The local changes are still preserved by Git and were not deleted.\n\n" +
            restore.Message);
    }

    private static async Task<GitSyncResult> RestoreSafetyStashAsync(
        string repositoryPath,
        bool stashCreated)
    {
        if (!stashCreated)
            return GitSyncResult.Ok("No safety stash needed.");

        var restore = await RunGitAsync(repositoryPath, "stash", "pop", "--index");
        if (restore.Success)
            return GitSyncResult.Ok("Local working files restored.");

        return GitSyncResult.Fail(
            "GitHub changes were applied, but one of the real local working files also changed on GitHub. " +
            "Git kept the safety stash so the local version is still recoverable. Code Loom will not choose between two C# versions automatically.\n\n" +
            restore.Output);
    }

    private static List<string> SplitPaths(string output)
    {
        return output
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Trim().Trim('"').Replace('\\', '/').TrimStart('/'))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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

    private static async Task<GitCommandResult> RunGitAsync(
        string workingDirectory,
        params string[] arguments)
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

            var stdout = (await outputTask).TrimEnd();
            var stderr = (await errorTask).Trim();
            return new GitCommandResult(process.ExitCode, stdout, stderr);
        }
        catch (Exception exception)
        {
            return new GitCommandResult(
                -1,
                string.Empty,
                "Could not run Git using " + GitExecutableLocator.DescribeResolvedPath() + ".\n" + exception.Message);
        }
    }

    private sealed record GitCommandResult(int ExitCode, string StdOut, string StdErr)
    {
        public bool Success => ExitCode == 0;
        public string Output => string.Join(
            Environment.NewLine,
            new[] { StdOut, StdErr }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }
}
