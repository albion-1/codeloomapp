using System.IO;
using System.Security.Cryptography;

namespace codeloomapp.Services;

public enum RepositoryCSharpChangeKind
{
    Added,
    Changed,
    Removed,
    Renamed
}

public sealed record RepositoryCSharpFile(string RelativePath, string Hash);

public sealed record RepositoryCSharpChange(
    string RelativePath,
    RepositoryCSharpChangeKind Kind,
    string PreviousRelativePath = "");

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
            var added = new List<RepositoryCSharpFile>();
            var removed = new List<RepositoryCSharpFile>();

            foreach (var file in current)
            {
                if (!previousSnapshot!.TryGetValue(file.RelativePath, out var previousHash))
                {
                    added.Add(file);
                }
                else if (!string.Equals(previousHash, file.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    changes.Add(new RepositoryCSharpChange(file.RelativePath, RepositoryCSharpChangeKind.Changed));
                }
            }

            foreach (var previous in previousSnapshot!)
            {
                if (!currentMap.ContainsKey(previous.Key))
                    removed.Add(new RepositoryCSharpFile(previous.Key, previous.Value));
            }

            // A unique same-content remove+add pair is a conservative rename signal.
            // Duplicate files are intentionally not guessed as moves.
            var addedByHash = added
                .GroupBy(file => file.Hash, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
            var removedByHash = removed
                .GroupBy(file => file.Hash, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);

            var pairedAdded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pairedRemoved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in removedByHash)
            {
                if (!addedByHash.TryGetValue(pair.Key, out var destination))
                    continue;

                changes.Add(new RepositoryCSharpChange(
                    destination.RelativePath,
                    RepositoryCSharpChangeKind.Renamed,
                    pair.Value.RelativePath));
                pairedAdded.Add(destination.RelativePath);
                pairedRemoved.Add(pair.Value.RelativePath);
            }

            changes.AddRange(added
                .Where(file => !pairedAdded.Contains(file.RelativePath))
                .Select(file => new RepositoryCSharpChange(file.RelativePath, RepositoryCSharpChangeKind.Added)));
            changes.AddRange(removed
                .Where(file => !pairedRemoved.Contains(file.RelativePath))
                .Select(file => new RepositoryCSharpChange(file.RelativePath, RepositoryCSharpChangeKind.Removed)));
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

    public static string HashFile(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
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
                    hash = HashFile(filePath);
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
