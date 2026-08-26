using System.Diagnostics;
using System.IO;

namespace codeloomapp.Services;

public sealed class GitHubCliService
{
    public async Task<GitHubCliResult> CheckAvailabilityAsync()
    {
        var result = await RunAsync(null, "--version");
        return result.Success
            ? GitHubCliResult.Ok(result.Output, "GitHub CLI")
            : GitHubCliResult.Fail(
                "GitHub CLI",
                "Code Loom could not start GitHub CLI. This release normally includes its own copy of gh.exe. " +
                "Reinstall the latest Code Loom build, or install GitHub CLI separately and restart Code Loom.",
                result.Output);
    }

    public async Task<GitHubCliResult> SignInAsync()
    {
        var available = await CheckAvailabilityAsync();
        if (!available.Success)
            return available;

        var existing = await VerifyAuthenticatedUserAsync();
        if (existing.Success)
            return await FinishAuthenticationAsync(existing.Message, null, "Already signed in to GitHub.");

        // Browser authentication must be interactive. More importantly, do not trust
        // only the helper process exit code: on some Windows setups gh can return a
        // non-zero exit after GitHub accepted the device code. We verify the account
        // with both `gh auth status` and a real API call before deciding it failed.
        var login = await RunInteractiveAsync(
            null,
            "auth login --hostname github.com --git-protocol https --web --skip-ssh-key");

        var verified = await VerifyAuthenticatedUserAsync();
        if (!verified.Success)
        {
            var helperDetail = string.IsNullOrWhiteSpace(login.Output)
                ? $"The browser sign-in helper exited with code {login.ExitCode}."
                : login.Output;

            return GitHubCliResult.Fail(
                "Browser sign-in verification",
                "GitHub did not leave Code Loom with a usable authenticated account after the browser/device-code flow. " +
                "You can retry the browser flow or use the Personal Access Token fallback.",
                helperDetail + Environment.NewLine + verified.DiagnosticDetails);
        }

        return await FinishAuthenticationAsync(
            verified.Message,
            login,
            "GitHub browser authentication completed.");
    }

    public async Task<GitHubCliResult> SignInWithTokenAsync(string token)
    {
        var available = await CheckAvailabilityAsync();
        if (!available.Success)
            return available;

        token = (token ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            return GitHubCliResult.Fail(
                "Personal Access Token",
                "Paste a GitHub Personal Access Token before continuing.");
        }

        if (token.Any(char.IsWhiteSpace))
        {
            return GitHubCliResult.Fail(
                "Personal Access Token",
                "The token contains whitespace. Copy the token again directly from GitHub and paste only the token value.");
        }

        // The token is sent through standard input. It is never placed in process
        // arguments, settings.json, project.json, status text, or Code Loom logs.
        var login = await RunWithStandardInputAsync(
            null,
            "auth login --hostname github.com --git-protocol https --with-token",
            token + Environment.NewLine);

        if (!login.Success)
        {
            return GitHubCliResult.Fail(
                "Personal Access Token acceptance",
                "GitHub CLI rejected the token. For the broadest Code Loom compatibility, use a classic Personal Access Token with the permissions GitHub CLI requires for the repositories you want to manage.",
                login.Output);
        }

        var verified = await VerifyAuthenticatedUserAsync();
        if (!verified.Success)
        {
            return GitHubCliResult.Fail(
                "Personal Access Token verification",
                "GitHub CLI accepted the token, but Code Loom could not verify it with the GitHub API.",
                verified.DiagnosticDetails);
        }

        return await FinishAuthenticationAsync(
            verified.Message,
            login,
            "GitHub token authentication completed.");
    }

    public async Task<GitHubCliResult> GetSignedInUserAsync()
    {
        var available = await CheckAvailabilityAsync();
        if (!available.Success)
            return available;

        return await VerifyAuthenticatedUserAsync();
    }

    public async Task<GitHubCliResult> CreateRepositoryAsync(
        string localFolder,
        string repositoryName,
        bool isPrivate,
        string description)
    {
        var signIn = await GetSignedInUserAsync();
        if (!signIn.Success)
            return GitHubCliResult.Fail("GitHub account", "Sign in to GitHub first.");

        Directory.CreateDirectory(localFolder);

        var gitAvailable = await CheckGitAvailabilityAsync();
        if (!gitAvailable.Success)
            return gitAvailable;

        if (!Directory.Exists(Path.Combine(localFolder, ".git")))
        {
            var init = await RunGitAsync(localFolder, "init -b main");
            if (!init.Success)
            {
                return GitHubCliResult.Fail(
                    "Local Git repository",
                    "Could not initialize the local Git repository.",
                    init.Output);
            }
        }

        var identity = await EnsureLocalGitIdentityAsync(localFolder, signIn.Message);
        if (!identity.Success)
            return identity;

        var add = await RunGitAsync(localFolder, "add --all");
        if (!add.Success)
            return GitHubCliResult.Fail("Git staging", "Could not stage project files.", add.Output);

        var status = await RunGitAsync(localFolder, "status --porcelain");
        if (!status.Success)
            return GitHubCliResult.Fail("Git status", "Could not inspect Git status.", status.Output);

        if (!string.IsNullOrWhiteSpace(status.Output))
        {
            var commit = await RunGitAsync(localFolder, "commit -m \"Initial Code Loom project\"");
            if (!commit.Success)
            {
                return GitHubCliResult.Fail(
                    "Initial Git commit",
                    "Could not create the initial commit.",
                    commit.Output);
            }
        }

        var visibility = isPrivate ? "--private" : "--public";
        var safeDescription = description.Replace("\"", "'");
        var arguments = $"repo create \"{repositoryName}\" {visibility} --source \"{localFolder}\" --remote origin --push";

        if (!string.IsNullOrWhiteSpace(safeDescription))
            arguments += $" --description \"{safeDescription}\"";

        var create = await RunAsync(null, arguments);
        if (!create.Success)
        {
            return GitHubCliResult.Fail(
                "GitHub repository creation",
                "GitHub repository creation failed.",
                create.Output);
        }

        return GitHubCliResult.Ok(create.Output, "GitHub repository creation");
    }

    private static async Task<GitHubCliResult> VerifyAuthenticatedUserAsync()
    {
        var status = await RunAsync(null, "auth status --hostname github.com");
        if (!status.Success)
        {
            return GitHubCliResult.Fail(
                "GitHub authentication status",
                "GitHub CLI does not currently report a valid github.com account.",
                status.Output);
        }

        // A successful status check alone can be stale or incomplete. This API request
        // proves that the credential actually works before Code Loom says Connected.
        var user = await RunAsync(null, "api user --jq .login");
        if (!user.Success || string.IsNullOrWhiteSpace(user.Output))
        {
            return GitHubCliResult.Fail(
                "GitHub API verification",
                "GitHub CLI reports an account, but the credential could not make an authenticated GitHub API request.",
                user.Output);
        }

        return GitHubCliResult.Ok(user.Output.Trim(), "GitHub API verification");
    }

    private static async Task<GitHubCliResult> FinishAuthenticationAsync(
        string githubLogin,
        CommandResult? loginCommand,
        string successMessage)
    {
        var warnings = new List<string>();

        if (loginCommand is { Success: false })
        {
            warnings.Add(
                $"The sign-in helper exited with code {loginCommand.ExitCode}, but GitHub accepted the authentication and Code Loom independently verified API access as {githubLogin}.");
        }

        var setup = await EnsureGitCredentialIntegrationAsync();
        if (!setup.Success)
        {
            // Authentication itself is still valid. This used to turn a successful
            // browser login into a misleading "GitHub sign-in failed" dialog.
            warnings.Add(setup.Message +
                         (string.IsNullOrWhiteSpace(setup.DiagnosticDetails)
                             ? string.Empty
                             : Environment.NewLine + setup.DiagnosticDetails));
        }

        return GitHubCliResult.Ok(
            $"{successMessage} Connected as {githubLogin}.",
            "Authentication complete",
            warnings.Count == 0 ? string.Empty : string.Join(Environment.NewLine + Environment.NewLine, warnings));
    }

    private static async Task<GitHubCliResult> EnsureGitCredentialIntegrationAsync()
    {
        var git = await CheckGitAvailabilityAsync();
        if (!git.Success)
        {
            return GitHubCliResult.Fail(
                "Git credential integration",
                "GitHub authentication succeeded, but Code Loom could not find Git. Sign-in is usable for GitHub API features, but Pull, Push, and New GitHub Project require Git to be installed or available through Visual Studio.",
                git.DiagnosticDetails);
        }

        // Configure normal HTTPS Git commands to ask the same authenticated GitHub CLI
        // for credentials. RunProcessAsync temporarily adds a discovered Git folder to
        // PATH so bundled/Visual-Studio Git installations can work even when `git` is
        // not globally registered in the user's PATH.
        var setup = await RunAsync(null, "auth setup-git");
        return setup.Success
            ? GitHubCliResult.Ok("Git credentials are connected to GitHub CLI.", "Git credential integration")
            : GitHubCliResult.Fail(
                "Git credential integration",
                "GitHub authentication succeeded, but Code Loom could not connect normal Git commands to that sign-in. Pull/Push may require attention.",
                setup.Output);
    }

    private static async Task<GitHubCliResult> CheckGitAvailabilityAsync()
    {
        var result = await RunGitAsync(Environment.CurrentDirectory, "--version");
        return result.Success
            ? GitHubCliResult.Ok(result.Output, "Git availability")
            : GitHubCliResult.Fail(
                "Git availability",
                "Code Loom could not start Git.",
                result.Output);
    }

    private static async Task<GitHubCliResult> EnsureLocalGitIdentityAsync(
        string localFolder,
        string githubLogin)
    {
        var currentName = await RunGitAsync(localFolder, "config user.name");
        if (!currentName.Success || string.IsNullOrWhiteSpace(currentName.Output))
        {
            var setName = await RunGitAsync(localFolder, $"config user.name \"{githubLogin}\"");
            if (!setName.Success)
            {
                return GitHubCliResult.Fail(
                    "Git author identity",
                    "Code Loom could not configure a local Git author name for the new repository.",
                    setName.Output);
            }
        }

        var currentEmail = await RunGitAsync(localFolder, "config user.email");
        if (!currentEmail.Success || string.IsNullOrWhiteSpace(currentEmail.Output))
        {
            var email = githubLogin + "@users.noreply.github.com";
            var setEmail = await RunGitAsync(localFolder, $"config user.email \"{email}\"");
            if (!setEmail.Success)
            {
                return GitHubCliResult.Fail(
                    "Git author identity",
                    "Code Loom could not configure a local Git author email for the new repository.",
                    setEmail.Output);
            }
        }

        return GitHubCliResult.Ok("Local Git identity is ready.", "Git author identity");
    }

    private static Task<CommandResult> RunGitAsync(string workingDirectory, string arguments) =>
        RunProcessAsync(ResolveGitExecutable(), arguments, workingDirectory);

    private static Task<CommandResult> RunAsync(string? workingDirectory, string arguments) =>
        RunProcessAsync(ResolveGitHubCliExecutable(), arguments, workingDirectory);

    private static Task<CommandResult> RunWithStandardInputAsync(
        string? workingDirectory,
        string arguments,
        string standardInput) =>
        RunProcessAsync(
            ResolveGitHubCliExecutable(),
            arguments,
            workingDirectory,
            standardInput);

    private static async Task<CommandResult> RunInteractiveAsync(
        string? workingDirectory,
        string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ResolveGitHubCliExecutable(),
                Arguments = arguments,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            };

            using var process = Process.Start(startInfo);
            if (process is null)
                return new CommandResult(false, "Windows could not open the GitHub sign-in helper.", -1);

            await process.WaitForExitAsync();
            return process.ExitCode == 0
                ? new CommandResult(true, "GitHub sign-in helper completed.", process.ExitCode)
                : new CommandResult(false, "The GitHub sign-in helper closed with a non-zero exit code.", process.ExitCode);
        }
        catch (Exception exception)
        {
            return new CommandResult(false, exception.Message, -1);
        }
    }

    private static string ResolveGitHubCliExecutable()
    {
        var bundled = Path.Combine(
            AppContext.BaseDirectory,
            "tools",
            "gh",
            "gh.exe");

        return File.Exists(bundled) ? bundled : "gh";
    }

    private static string ResolveGitExecutable()
    {
        var pathGit = FindExecutableOnPath("git.exe");
        if (!string.IsNullOrWhiteSpace(pathGit))
            return pathGit;

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string>
        {
            Path.Combine(programFiles, "Git", "cmd", "git.exe"),
            Path.Combine(programFiles, "Git", "bin", "git.exe"),
            Path.Combine(localAppData, "Programs", "Git", "cmd", "git.exe")
        };

        // Visual Studio commonly carries its own Git. Code Loom can use it even when
        // the installer did not add Git to the global PATH.
        foreach (var version in new[] { "2026", "18", "2022" })
        {
            foreach (var edition in new[] { "Community", "Professional", "Enterprise", "BuildTools" })
            {
                candidates.Add(Path.Combine(
                    programFiles,
                    "Microsoft Visual Studio",
                    version,
                    edition,
                    "Common7",
                    "IDE",
                    "CommonExtensions",
                    "Microsoft",
                    "TeamFoundation",
                    "Team Explorer",
                    "Git",
                    "cmd",
                    "git.exe"));
            }
        }

        return candidates.FirstOrDefault(File.Exists) ?? "git";
    }

    private static string? FindExecutableOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        foreach (var part in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var folder = part.Trim().Trim('"');
                var candidate = Path.Combine(folder, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // Ignore malformed PATH entries and keep searching.
            }
        }

        return null;
    }

    private static async Task<CommandResult> RunProcessAsync(
        string fileName,
        string arguments,
        string? workingDirectory,
        string? standardInput = null)
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
                RedirectStandardInput = standardInput is not null,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            AddDiscoveredGitToPath(startInfo);

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput);
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync();

            var output = (await outputTask).Trim();
            var error = (await errorTask).Trim();
            var combined = string.Join(Environment.NewLine,
                new[] { output, error }.Where(value => !string.IsNullOrWhiteSpace(value)));

            return new CommandResult(process.ExitCode == 0, combined, process.ExitCode);
        }
        catch (Exception exception)
        {
            return new CommandResult(false, exception.Message, -1);
        }
    }

    private static void AddDiscoveredGitToPath(ProcessStartInfo startInfo)
    {
        var git = ResolveGitExecutable();
        if (!Path.IsPathFullyQualified(git) || !File.Exists(git))
            return;

        var gitDirectory = Path.GetDirectoryName(git);
        if (string.IsNullOrWhiteSpace(gitDirectory))
            return;

        var existing = startInfo.Environment.TryGetValue("PATH", out var path)
            ? path
            : Environment.GetEnvironmentVariable("PATH");
        startInfo.Environment["PATH"] = string.IsNullOrWhiteSpace(existing)
            ? gitDirectory
            : gitDirectory + Path.PathSeparator + existing;
    }

    private sealed record CommandResult(bool Success, string Output, int ExitCode);
}

public sealed record GitHubCliResult(
    bool Success,
    string Message,
    string Stage = "",
    string Warning = "",
    string DiagnosticDetails = "")
{
    public bool HasWarning => !string.IsNullOrWhiteSpace(Warning);

    public static GitHubCliResult Ok(
        string message,
        string stage = "",
        string warning = "") =>
        new(true, message, stage, warning, string.Empty);

    public static GitHubCliResult Fail(
        string stage,
        string message,
        string diagnosticDetails = "") =>
        new(false, message, stage, string.Empty, diagnosticDetails ?? string.Empty);
}
