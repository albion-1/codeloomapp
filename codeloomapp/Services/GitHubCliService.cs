using System.Diagnostics;
using System.IO;

namespace codeloomapp.Services;

public sealed class GitHubCliService
{
    public async Task<GitHubCliResult> CheckAvailabilityAsync()
    {
        var result = await RunAsync(null, "--version");
        return result.Success
            ? GitHubCliResult.Ok(result.Output)
            : GitHubCliResult.Fail("GitHub CLI (gh) is not installed or not available in PATH. Install it from https://cli.github.com/ and restart Code Loom.");
    }

    public async Task<GitHubCliResult> SignInAsync()
    {
        var available = await CheckAvailabilityAsync();
        if (!available.Success)
            return available;

        var status = await RunAsync(null, "auth status");
        if (status.Success)
            return GitHubCliResult.Ok("Already signed in to GitHub.");

        // --web opens the user's normal browser. GitHub CLI stores the credential securely.
        var login = await RunAsync(null, "auth login --hostname github.com --git-protocol https --web");
        if (!login.Success)
            return GitHubCliResult.Fail("GitHub sign-in failed:\n" + login.Output);

        return GitHubCliResult.Ok("GitHub sign-in completed.");
    }

    public async Task<GitHubCliResult> GetSignedInUserAsync()
    {
        var status = await RunAsync(null, "auth status");
        if (!status.Success)
            return GitHubCliResult.Fail("Not signed in to GitHub.");

        var user = await RunAsync(null, "api user --jq .login");
        return user.Success && !string.IsNullOrWhiteSpace(user.Output)
            ? GitHubCliResult.Ok(user.Output.Trim())
            : GitHubCliResult.Fail("Could not read the signed-in GitHub account.");
    }

    public async Task<GitHubCliResult> CreateRepositoryAsync(
        string localFolder,
        string repositoryName,
        bool isPrivate,
        string description)
    {
        var signIn = await GetSignedInUserAsync();
        if (!signIn.Success)
            return GitHubCliResult.Fail("Sign in to GitHub first.");

        Directory.CreateDirectory(localFolder);

        if (!Directory.Exists(Path.Combine(localFolder, ".git")))
        {
            var init = await RunGitAsync(localFolder, "init -b main");
            if (!init.Success)
                return GitHubCliResult.Fail("Could not initialize the local Git repository:\n" + init.Output);
        }

        var add = await RunGitAsync(localFolder, "add --all");
        if (!add.Success)
            return GitHubCliResult.Fail("Could not stage project files:\n" + add.Output);

        var status = await RunGitAsync(localFolder, "status --porcelain");
        if (!status.Success)
            return GitHubCliResult.Fail("Could not inspect Git status:\n" + status.Output);

        if (!string.IsNullOrWhiteSpace(status.Output))
        {
            var commit = await RunGitAsync(localFolder, "commit -m \"Initial Code Loom project\"");
            if (!commit.Success)
                return GitHubCliResult.Fail("Could not create the initial commit:\n" + commit.Output);
        }

        var visibility = isPrivate ? "--private" : "--public";
        var safeDescription = description.Replace("\"", "'");
        var arguments = $"repo create \"{repositoryName}\" {visibility} --source \"{localFolder}\" --remote origin --push";

        if (!string.IsNullOrWhiteSpace(safeDescription))
            arguments += $" --description \"{safeDescription}\"";

        var create = await RunAsync(null, arguments);
        if (!create.Success)
            return GitHubCliResult.Fail("GitHub repository creation failed:\n" + create.Output);

        return GitHubCliResult.Ok(create.Output);
    }

    private static Task<CommandResult> RunGitAsync(string workingDirectory, string arguments) =>
        RunProcessAsync("git", arguments, workingDirectory);

    private static Task<CommandResult> RunAsync(string? workingDirectory, string arguments) =>
        RunProcessAsync("gh", arguments, workingDirectory);

    private static async Task<CommandResult> RunProcessAsync(string fileName, string arguments, string? workingDirectory)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
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
                new[] { output, error }.Where(value => !string.IsNullOrWhiteSpace(value)));

            return new CommandResult(process.ExitCode == 0, combined);
        }
        catch (Exception exception)
        {
            return new CommandResult(false, exception.Message);
        }
    }

    private sealed record CommandResult(bool Success, string Output);
}

public sealed record GitHubCliResult(bool Success, string Message)
{
    public static GitHubCliResult Ok(string message) => new(true, message);
    public static GitHubCliResult Fail(string message) => new(false, message);
}
