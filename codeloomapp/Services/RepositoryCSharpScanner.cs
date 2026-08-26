using System.IO;
using System.Security.Cryptography;

namespace codeloomapp.Services;

public enum RepositoryCSharpChangeKind
{
    Added,
    Changed,
    Removed
}

public sealed record RepositoryCSharpFile(string RelativePath, string Hash);

public sealed record RepositoryCSharpChange(
    string RelativePath,
    RepositoryCSharpChangeKind Kind);

public sealed class RepositoryCSharpScanResult
{
    public IReadOnlyList<RepositoryCSharpFile> Files { get; init; } = Array.Empty<RepositoryCSharpFile>();
    public IReadOnlyList<RepositoryCSharpChange> Changes { get; init; } = Array.Empty<RepositoryCSharpChange>();
    public bool IsFirstScan { get; init; }
}

public sealed class RepositoryCSharpScanner
{
    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".codeloom",
        "Library",
        "Temp",
        "Logs",
        "UserSettings",
        "Packages",
        "obj",
        "bin"
    };

    public RepositoryCSharpScanResult Scan(
        string repositoryPath,
        IReadOnlyDictionary<string, string>? previousSnapshot = null)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
            throw new ArgumentException("Repository path is required.", nameof(repositoryPath));

        var root = Path.GetFullPath(repositoryPath);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Repository folder does not exist: {root}");

        var current = EnumerateCSharpFiles(root)
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var currentMap = current.ToDictionary(
            file => file.RelativePath,
            file => file.Hash,
            StringComparer.OrdinalIgnoreCase);

        var firstScan = previousSnapshot is null;
        var changes = new List<RepositoryCSharpChange>();

        if (firstScan)
        {
            changes.AddRange(current.Select(file =>
                new RepositoryCSharpChange(file.RelativePath, RepositoryCSharpChangeKind.Added)));
        }
        else
        {
            foreach (var file in current)
            {
                if (!previousSnapshot!.TryGetValue(file.RelativePath, out var previousHash))
                {
                    changes.Add(new RepositoryCSharpChange(file.RelativePath, RepositoryCSharpChangeKind.Added));
                }
                else if (!string.Equals(previousHash, file.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    changes.Add(new RepositoryCSharpChange(file.RelativePath, RepositoryCSharpChangeKind.Changed));
                }
            }

            foreach (var oldPath in previousSnapshot!.Keys)
            {
                if (!currentMap.ContainsKey(oldPath))
                    changes.Add(new RepositoryCSharpChange(oldPath, RepositoryCSharpChangeKind.Removed));
            }
        }

        return new RepositoryCSharpScanResult
        {
            Files = current,
            Changes = changes
                .OrderBy(change => change.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            IsFirstScan = firstScan
        };
    }

    public static Dictionary<string, string> CreateSnapshot(RepositoryCSharpScanResult result)
    {
        return result.Files.ToDictionary(
            file => file.RelativePath,
            file => file.Hash,
            StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<RepositoryCSharpFile> EnumerateCSharpFiles(string repositoryRoot)
    {
        var pending = new Stack<string>();
        pending.Push(repositoryRoot);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            string[] childDirectories;
            string[] files;
            try
            {
                childDirectories = Directory.GetDirectories(directory);
                files = Directory.GetFiles(directory, "*.cs", SearchOption.TopDirectoryOnly);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var child in childDirectories)
            {
                var name = Path.GetFileName(child);
                if (ExcludedDirectoryNames.Contains(name))
                    continue;

                if (IsGeneratedCodeLoomDirectory(repositoryRoot, child))
                    continue;

                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                        continue;
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                pending.Push(child);
            }

            foreach (var filePath in files)
            {
                string hash;
                try
                {
                    using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    hash = Convert.ToHexString(SHA256.HashData(stream));
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(repositoryRoot, filePath)
                    .Replace(Path.DirectorySeparatorChar, '/');

                yield return new RepositoryCSharpFile(relativePath, hash);
            }
        }
    }

    private static bool IsGeneratedCodeLoomDirectory(string repositoryRoot, string directory)
    {
        var relative = Path.GetRelativePath(repositoryRoot, directory)
            .Replace(Path.DirectorySeparatorChar, '/');

        return relative.Equals("Assets/CodeLoom/Generated", StringComparison.OrdinalIgnoreCase)
               || relative.StartsWith("Assets/CodeLoom/Generated/", StringComparison.OrdinalIgnoreCase);
    }
}
