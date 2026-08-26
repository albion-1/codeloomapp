using System.Diagnostics;
using codeloomapp.Services;

internal static class RepositoryGitWorkflowSmokeTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeLoomGitWorkflowSmoke-" + Guid.NewGuid().ToString("N"));
        var remote = Path.Combine(root, "remote.git");
        var writer = Path.Combine(root, "writer");
        var local = Path.Combine(root, "local");
        var verification = Path.Combine(root, "verification");
        Directory.CreateDirectory(root);

        try
        {
            Git(root, "init", "--bare", "--initial-branch=master", remote);

            Directory.CreateDirectory(writer);
            Git(writer, "init", "-b", "master");
            ConfigureIdentity(writer);
            Git(writer, "remote", "add", "origin", remote);

            Write(writer, "Assets/Scripts/PlayerMovement.cs", "public class PlayerMovement { }\n");
            Write(writer, ".codeloom/project.json", "{\"SchemaVersion\":2,\"Name\":\"Smoke\",\"Files\":[]}\n");
            Git(writer, "add", "--all");
            Git(writer, "commit", "-m", "initial source");
            Git(writer, "push", "-u", "origin", "master");

            Git(root, "clone", remote, local);
            ConfigureIdentity(local);

            Write(local, ".codeloom/project.json", "{\"localMetadata\":true}\n");

            File.Delete(Path.Combine(writer, "Assets", "Scripts", "PlayerMovement.cs"));
            Write(writer, "Assets/Scripts/PlayerCamera.cs", "public class PlayerCamera { }\n");
            Write(writer, "Assets/Scripts/BattleController.cs", "public class BattleController { }\n");
            Git(writer, "add", "--all");
            Git(writer, "commit", "-m", "replace source layout");
            Git(writer, "push");

            var pull = new RepositoryGitPullService().PullAsync(local).GetAwaiter().GetResult();
            Assert(pull.Success, "Pull should protect local uncommitted metadata: " + pull.Message);
            Assert(!File.Exists(Path.Combine(local, "Assets", "Scripts", "PlayerMovement.cs")),
                "remote deletion should be reflected locally after Pull");
            Assert(File.Exists(Path.Combine(local, "Assets", "Scripts", "PlayerCamera.cs")),
                "new PlayerCamera.cs should arrive through Pull");
            Assert(File.Exists(Path.Combine(local, "Assets", "Scripts", "BattleController.cs")),
                "new BattleController.cs should arrive through Pull");
            Assert(File.ReadAllText(Path.Combine(local, ".codeloom", "project.json"))
                    .Contains("\"localMetadata\":true", StringComparison.Ordinal),
                "local uncommitted Code Loom metadata should be restored after Pull");

            Git(local, "restore", "--", ".codeloom/project.json");
            Write(local, "notes.txt", "unrelated local note\n");
            Write(local, "Assets/Scripts/PlayerCamera.cs", "public class PlayerCamera { /* Code Loom edit */ }\n");

            var push = new RepositoryGitSyncService().SyncAsync(local).GetAwaiter().GetResult();
            Assert(push.Success, "Push should ignore unrelated local files instead of demanding deletion: " + push.Message);
            Assert(File.Exists(Path.Combine(local, "notes.txt")),
                "unrelated local file should remain after Push");

            var localStatus = Git(local, "status", "--porcelain=v1", "--untracked-files=all");
            Assert(localStatus.Contains("notes.txt", StringComparison.Ordinal),
                "unrelated local file should remain uncommitted after Code Loom Push");

            Git(root, "clone", remote, verification);
            var pushedCamera = File.ReadAllText(Path.Combine(verification, "Assets", "Scripts", "PlayerCamera.cs"));
            Assert(pushedCamera.Contains("Code Loom edit", StringComparison.Ordinal),
                "managed C# edit should reach the remote repository");
            Assert(!File.Exists(Path.Combine(verification, "notes.txt")),
                "unrelated local file must not be accidentally included in Code Loom Push");
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static void ConfigureIdentity(string repository)
    {
        Git(repository, "config", "user.name", "Code Loom Smoke Test");
        Git(repository, "config", "user.email", "codeloom-smoke@example.invalid");
    }

    private static void Write(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string Git(string workingDirectory, params string[] arguments)
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
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        var combined = string.Join(
            Environment.NewLine,
            new[] { output.Trim(), error.Trim() }.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed with exit code {process.ExitCode}:\n{combined}");
        }

        return combined;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
