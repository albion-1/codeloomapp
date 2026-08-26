using System.Diagnostics;

namespace codeloomapp.Services;

public sealed class RepositoryGitPullService
{
    public async Task<GitSyncResult> PullAsync(string repositoryPath)
    {
        var initial = await RunGitAsync(
            repositoryPath,
            "status",
            "--porcelain=v1",
            "--untracked-files=all");
        if (!initial.Success)
            return GitSyncResult.Fail("Could not inspect the local repository before Pull:\n" + initial.Output);

        var hadLocalChanges = !string.IsNullOrWhiteSpace(initial.Output);
        var stashCreated = false;

        if (hadLocalChanges)
        {
            var stash = await RunGitAsync(
                repositoryPath,
                "stash",
                "push",
                "--include-untracked",
                "--message",
                "Code Loom automatic pull safety");
            if (!stash.Success)
            {
                return GitSyncResult.Fail(
                    "Code Loom found local working files, but Git could not temporarily protect them before Pull. " +
                    "Nothing was deleted or overwritten.\n\n" + stash.Output);
            }

            stashCreated = !stash.Output.Contains(
                "No local changes to save",
                StringComparison.OrdinalIgnoreCase);
        }

        var fetch = await RunGitAsync(repositoryPath, "fetch", "--prune");
        if (!fetch.Success)
            return await FailAndRestoreAsync(repositoryPath, stashCreated, "Could not refresh GitHub before Pull:\n" + fetch.Output);

        var branch = await RunGitAsync(repositoryPath, "symbolic-ref", "--quiet", "--short", "HEAD");
        if (!branch.Success || string.IsNullOrWhiteSpace(branch.Output))
            return await FailAndRestoreAsync(repositoryPath, stashCreated, "Check out a normal branch before Pull.");

        var upstream = await RunGitAsync(
            repositoryPath,
            "rev-parse",
            "--abbrev-ref",
            "--symbolic-full-name",
            "@{upstream}");
        if (!upstream.Success || string.IsNullOrWhiteSpace(upstream.Output))
        {
            return await FailAndRestoreAsync(
                repositoryPath,
                stashCreated,
                "This branch has no upstream GitHub branch to pull from.");
        }

        var counts = await RunGitAsync(
            repositoryPath,
            "rev-list",
            "--left-right",
            "--count",
            $"HEAD...{upstream.Output.Trim()}");
        if (!counts.Success)
            return await FailAndRestoreAsync(repositoryPath, stashCreated, "Could not compare the local branch with GitHub:\n" + counts.Output);

        ParseAheadBehind(counts.Output, out var ahead, out var behind);
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
            var rebase = await RunGitAsync(repositoryPath, "rebase", upstream.Output.Trim());
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
                upstream.Output.Trim());
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
            "GitHub changes may already be present locally, but Git found a conflict while restoring the files that existed before Pull. " +
            "Git keeps the safety stash when this happens, so those local files are still recoverable. " +
            "Code Loom did not intentionally delete them.\n\n" + restore.Output);
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

            var output = (await outputTask).Trim();
            var error = (await errorTask).Trim();
            return new GitCommandResult(
                process.ExitCode,
                string.Join(
                    Environment.NewLine,
                    new[] { output, error }.Where(value => !string.IsNullOrWhiteSpace(value))));
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
