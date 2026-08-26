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

            // Code Loom can regenerate project.json locally without that generated
            // metadata being allowed to block a later Pull.
            Write(local, ".codeloom/project.json", "{\"localGeneratedMetadata\":true}\n");

            File.Delete(Path.Combine(writer, "Assets", "Scripts", "PlayerMovement.cs"));
            Write(writer, "Assets/Scripts/PlayerCamera.cs", "public class PlayerCamera { }\n");
            Write(writer, "Assets/Scripts/BattleController.cs", "public class BattleController { }\n");
            Write(writer, ".codeloom/project.json", "{\"SchemaVersion\":2,\"Name\":\"Remote Layout\",\"Files\":[]}\n");
            Git(writer, "add", "--all");
            Git(writer, "commit", "-m", "replace source layout");
            Git(writer, "push");

            var pull = new RepositoryGitPullService().PullAsync(local).GetAwaiter().GetResult();
            Assert(pull.Success, "Pull should ignore locally regenerated project metadata: " + pull.Message);
            Assert(!File.Exists(Path.Combine(local, "Assets", "Scripts", "PlayerMovement.cs")),
                "remote deletion should be reflected locally after Pull");
            Assert(File.Exists(Path.Combine(local, "Assets", "Scripts", "PlayerCamera.cs")),
                "new PlayerCamera.cs should arrive through Pull");
            Assert(File.Exists(Path.Combine(local, "Assets", "Scripts", "BattleController.cs")),
                "new BattleController.cs should arrive through Pull");
            Assert(File.ReadAllText(Path.Combine(local, ".codeloom", "project.json"))
                    .Contains("Remote Layout", StringComparison.Ordinal),
                "remote project metadata should remain after Pull instead of restoring stale JSON bytes");

            // Reproduce the exact 0.1.7 failure: a safety stash containing project.json
            // is popped after GitHub changed the same generated metadata, leaving one
            // unresolved metadata conflict and the safety stash still present.
            Write(local, ".codeloom/project.json", "{\"oldLocalMetadata\":true}\n");
            Git(local, "stash", "push", "--include-untracked", "--message", "Code Loom automatic pull safety");

            Write(writer, ".codeloom/project.json", "{\"SchemaVersion\":2,\"Name\":\"Remote Metadata 2\",\"Files\":[]}\n");
            Git(writer, "add", ".codeloom/project.json");
            Git(writer, "commit", "-m", "update metadata again");
            Git(writer, "push");

            Git(local, "fetch", "--prune");
            Git(local, "merge", "--ff-only", "origin/master");
            var failedPop = GitAllowFailure(local, "stash", "pop", "--index");
            Assert(failedPop.ExitCode != 0, "test setup should create the old project.json stash-pop conflict");
            Assert(Git(local, "diff", "--name-only", "--diff-filter=U")
                    .Replace('\\', '/')
                    .Contains(".codeloom/project.json", StringComparison.OrdinalIgnoreCase),
                "test setup should leave project.json unresolved");

            var repaired = RepositoryGitPullService.RepairCodeLoomMetadataConflictAsync(local).GetAwaiter().GetResult();
            Assert(repaired.Success, "Code Loom should automatically repair its own metadata-only conflict: " + repaired.Message);
            Assert(string.IsNullOrWhiteSpace(Git(local, "diff", "--name-only", "--diff-filter=U")),
                "metadata repair should leave no unresolved Git files");
            Assert(File.ReadAllText(Path.Combine(local, ".codeloom", "project.json"))
                    .Contains("Remote Metadata 2", StringComparison.Ordinal),
                "metadata repair should keep the current branch version of generated project.json");

            Write(local, "notes.txt", "unrelated local note\n");
            Write(local, ".codeloom/project.legacy-backup.json", "{\"legacyLocalOnly\":true}\n");
            Write(local, "Assets/Scripts/PlayerCamera.cs", "public class PlayerCamera { /* Code Loom edit */ }\n");

            var push = new RepositoryGitSyncService().SyncAsync(local).GetAwaiter().GetResult();
            Assert(push.Success, "Push should ignore unrelated/local-only files instead of demanding deletion: " + push.Message);
            Assert(File.Exists(Path.Combine(local, "notes.txt")),
                "unrelated local file should remain after Push");
            Assert(File.Exists(Path.Combine(local, ".codeloom", "project.legacy-backup.json")),
                "legacy migration backup should remain local after Push");

            var localStatus = Git(local, "status", "--porcelain=v1", "--untracked-files=all");
            Assert(localStatus.Contains("notes.txt", StringComparison.Ordinal),
                "unrelated local file should remain uncommitted after Code Loom Push");

            Git(root, "clone", remote, verification);
            var pushedCamera = File.ReadAllText(Path.Combine(verification, "Assets", "Scripts", "PlayerCamera.cs"));
            Assert(pushedCamera.Contains("Code Loom edit", StringComparison.Ordinal),
                "managed C# edit should reach the remote repository");
            Assert(!File.Exists(Path.Combine(verification, "notes.txt")),
                "unrelated local file must not be accidentally included in Code Loom Push");
            Assert(!File.Exists(Path.Combine(verification, ".codeloom", "project.legacy-backup.json")),
                "local migration backup must never be pushed to GitHub");
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
        var result = GitAllowFailure(workingDirectory, arguments);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed with exit code {result.ExitCode}:\n{result.Output}");
        }

        return result.Output;
    }

    private static GitTestResult GitAllowFailure(string workingDirectory, params string[] arguments)
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
            new[] { output.TrimEnd(), error.Trim() }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return new GitTestResult(process.ExitCode, combined);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record GitTestResult(int ExitCode, string Output);
}
