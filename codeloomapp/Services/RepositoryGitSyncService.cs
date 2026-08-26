using System.Diagnostics;

namespace codeloomapp.Services;

public sealed class RepositoryGitSyncService
{
    public async Task<GitSyncResult> SyncAsync(string repositoryPath)
    {
        var status = await RunGitAsync(repositoryPath, "status", "--porcelain=v1", "--untracked-files=all");
        if (!status.Success)
            return GitSyncResult.Fail("Could not inspect Git status:\n" + status.Output);

        var changedPaths = ParseChangedPaths(status.Output).ToList();
        var unrelated = changedPaths.Where(path => !IsManagedPath(path)).ToList();
        if (unrelated.Count > 0)
        {
            var shown = string.Join("\n", unrelated.Take(8).Select(path => "• " + path));
            return GitSyncResult.Fail(
                "Code Loom found local changes outside physical C# source and its own metadata/export paths. " +
                "Those files were left untouched. Commit, stash, or discard them before Push.\n\n" + shown);
        }

        foreach (var path in changedPaths)
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

            var publish = await RunGitAsync(repositoryPath, "push", "-u", "origin", "HEAD");
            return publish.Success
                ? GitSyncResult.Ok(committed
                    ? "Committed physical C# source and published the branch to GitHub."
                    : "Published the branch and confirmed GitHub is up to date.")
                : GitSyncResult.Fail("Push failed:\n" + publish.Output);
        }

        var counts = await RunGitAsync(
            repositoryPath,
            "rev-list",
            "--left-right",
            "--count",
            $"HEAD...{upstream.Output.Trim()}");
        var behind = 0;
        if (counts.Success)
        {
            var parts = counts.Output.Split(
                new[] { ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                _ = int.TryParse(parts[1], out behind);
        }

        if (behind > 0)
        {
            var rebase = await RunGitAsync(repositoryPath, "rebase", upstream.Output.Trim());
            if (!rebase.Success)
            {
                return GitSyncResult.Fail(
                    "Push paused because local and GitHub commits could not be reconciled automatically. " +
                    "Resolve the Git conflict before pushing again.\n\n" + rebase.Output);
            }
        }

        var push = await RunGitAsync(repositoryPath, "push");
        if (!push.Success)
            return GitSyncResult.Fail("Push failed:\n" + push.Output);

        return GitSyncResult.Ok(committed
            ? "Committed physical C# source, incorporated GitHub updates, and pushed successfully."
            : "GitHub is up to date.");
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
        return normalizedPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
               || normalizedPath.StartsWith(".codeloom/", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalizedPath, ".codeloom", StringComparison.OrdinalIgnoreCase)
               || normalizedPath.StartsWith(UnityExportService.GeneratedRelativePath + "/", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalizedPath, UnityExportService.GeneratedRelativePath, StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalizedPath, UnityExportService.GeneratedRelativePath + ".meta", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalizedPath, "Assets/CodeLoom.meta", StringComparison.OrdinalIgnoreCase);
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
            return new GitCommandResult(
                process.ExitCode,
                string.Join(Environment.NewLine, new[] { output, error }.Where(value => !string.IsNullOrWhiteSpace(value))));
        }
        catch (Exception exception)
        {
            return new GitCommandResult(-1, "Could not run Git.\n" + exception.Message);
        }
    }

    private sealed record GitCommandResult(int ExitCode, string Output)
    {
        public bool Success => ExitCode == 0;
    }
}
