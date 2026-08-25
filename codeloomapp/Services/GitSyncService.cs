using System.Diagnostics;
using System.IO;

namespace codeloomapp.Services;

public sealed class GitSyncService
{
    public bool IsGitRepository(string path)
    {
        return Directory.Exists(path) && Directory.Exists(Path.Combine(path, ".git"));
    }

    public async Task<GitSyncResult> SyncAsync(string repositoryPath)
    {
        if (!IsGitRepository(repositoryPath))
            return GitSyncResult.Fail("The selected folder is not a Git repository.");

        var add = await RunGitAsync(repositoryPath, "add --all");
        if (!add.Success)
            return GitSyncResult.Fail("Could not stage local changes:\n" + add.Output);

        var status = await RunGitAsync(repositoryPath, "status --porcelain");
        if (!status.Success)
            return GitSyncResult.Fail("Could not inspect Git status:\n" + status.Output);

        var committed = false;
        if (!string.IsNullOrWhiteSpace(status.Output))
        {
            var message = $"Code Loom sync {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            var commit = await RunGitAsync(repositoryPath, $"commit -m \"{message}\"");
            if (!commit.Success)
                return GitSyncResult.Fail("Commit failed:\n" + commit.Output);

            committed = true;
        }

        var pull = await RunGitAsync(repositoryPath, "pull --rebase");
        if (!pull.Success)
            return GitSyncResult.Fail("Pull/rebase failed. Code Loom did not push anything.\n" + pull.Output);

        var push = await RunGitAsync(repositoryPath, "push");
        if (!push.Success)
            return GitSyncResult.Fail("Push failed:\n" + push.Output);

        return GitSyncResult.Ok(committed
            ? "Saved, committed, pulled remote changes, and pushed to GitHub."
            : "Pulled remote changes and confirmed GitHub is up to date.");
    }

    private static async Task<GitCommandResult> RunGitAsync(string workingDirectory, string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            var output = (await outputTask).Trim();
            var error = (await errorTask).Trim();
            var combined = string.Join(Environment.NewLine,
                new[] { output, error }.Where(text => !string.IsNullOrWhiteSpace(text)));

            return new GitCommandResult(process.ExitCode == 0, combined);
        }
        catch (Exception exception)
        {
            return new GitCommandResult(false,
                "Could not run Git. Make sure Git is installed and available in PATH.\n" + exception.Message);
        }
    }

    private sealed record GitCommandResult(bool Success, string Output);
}

public sealed record GitSyncResult(bool Success, string Message)
{
    public static GitSyncResult Ok(string message) => new(true, message);
    public static GitSyncResult Fail(string message) => new(false, message);
}
