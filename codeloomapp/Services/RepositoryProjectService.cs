using System.Security.Cryptography;
using System.Text;
using codeloomapp.Models;

namespace codeloomapp.Services;

public sealed class RepositoryProjectService
{
    private readonly RepositoryCSharpScanner _scanner = new();

    public RepositoryProjectLoadResult Load(
        string repositoryPath,
        RepositoryMetadataLoadResult stored)
    {
        var root = Path.GetFullPath(repositoryPath);
        var metadata = stored.Metadata;
        var previousSnapshot = metadata?.Files
            .Where(file => !string.IsNullOrWhiteSpace(file.RelativePath)
                           && !string.IsNullOrWhiteSpace(file.LastKnownHash))
            .GroupBy(file => NormalizeRelativePath(file.RelativePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().LastKnownHash, StringComparer.OrdinalIgnoreCase);

        var scan = _scanner.Scan(root, previousSnapshot is { Count: > 0 } ? previousSnapshot : null);
        var warnings = new List<string>();

        var metadataByPath = metadata?.Files
            .Where(file => !string.IsNullOrWhiteSpace(file.RelativePath))
            .GroupBy(file => NormalizeRelativePath(file.RelativePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, CodeLoomFileMetadata>(StringComparer.OrdinalIgnoreCase);

        // Carry Code Loom-only metadata through a physical rename when the scanner can
        // confidently pair the old and new paths by a unique identical file hash.
        foreach (var rename in scan.Changes.Where(change => change.Kind == RepositoryCSharpChangeKind.Renamed))
        {
            if (metadataByPath.TryGetValue(rename.PreviousRelativePath, out var movedMetadata)
                && !metadataByPath.ContainsKey(rename.RelativePath))
            {
                metadataByPath[rename.RelativePath] = movedMetadata;
            }
        }

        var project = new CodeProject
        {
            SchemaVersion = 2,
            Name = !string.IsNullOrWhiteSpace(metadata?.Name)
                ? metadata.Name
                : !string.IsNullOrWhiteSpace(stored.LegacyProject?.Name)
                    ? stored.LegacyProject.Name
                    : Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        };

        var legacyFiles = stored.LegacyProject?.Folders
            .SelectMany(folder => folder.Files.Select(file => (Folder: folder, File: file)))
            .ToList()
            ?? new List<(CodeFolder Folder, CodeFile File)>();
        var matchedLegacy = new HashSet<CodeFile>();

        foreach (var discovered in scan.Files)
        {
            var fullPath = ResolveRepositoryPath(root, discovered.RelativePath);
            try
            {
                var source = File.ReadAllText(fullPath);
                var import = CSharpImportService.Import(source, Path.GetFileName(discovered.RelativePath));
                var file = import.File;
                file.RepositoryRelativePath = NormalizeRelativePath(discovered.RelativePath);
                file.SourceHash = discovered.Hash;
                file.IsRepositoryBacked = true;
                file.IsLegacyUnmapped = false;

                if (metadataByPath.TryGetValue(file.RepositoryRelativePath, out var fileMetadata))
                {
                    ApplyMetadata(file, fileMetadata);
                }
                else
                {
                    // Migration path for old project.json files: if exactly one legacy
                    // Code Loom file has this physical file name, preserve its descriptions
                    // and manual assembly choices while taking code from the real .cs file.
                    var candidates = legacyFiles
                        .Where(candidate => !matchedLegacy.Contains(candidate.File)
                                            && string.Equals(
                                                candidate.File.Name,
                                                file.Name,
                                                StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (candidates.Count == 1)
                    {
                        ApplyLegacyMetadata(file, candidates[0].File);
                        matchedLegacy.Add(candidates[0].File);
                    }
                }

                foreach (var warning in import.Warnings)
                    warnings.Add($"{file.RepositoryRelativePath}: {warning}");

                AddToPhysicalFolder(project, file);
            }
            catch (Exception exception)
            {
                warnings.Add($"{discovered.RelativePath}: {exception.Message}");
            }
        }

        // Never discard an older Code Loom-only file simply because no physical source
        // could be matched. It remains visible in-memory and the original full JSON is
        // backed up before project.json is converted to metadata-only format.
        foreach (var legacy in legacyFiles.Where(candidate => !matchedLegacy.Contains(candidate.File)))
        {
            legacy.File.IsRepositoryBacked = false;
            legacy.File.IsLegacyUnmapped = true;
            legacy.File.RepositoryRelativePath = string.Empty;

            var folderName = string.IsNullOrWhiteSpace(legacy.Folder.Name)
                ? "Legacy (unmapped)"
                : $"Legacy (unmapped)/{legacy.Folder.Name}";
            var folder = project.Folders.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, folderName, StringComparison.OrdinalIgnoreCase));
            if (folder is null)
            {
                folder = new CodeFolder { Name = folderName };
                project.Folders.Add(folder);
            }
            folder.Files.Add(legacy.File);
        }

        SortProject(project);
        return new RepositoryProjectLoadResult(project, scan, warnings, stored.IsLegacyProject);
    }

    public RepositoryProjectSaveResult Save(
        CodeProject project,
        string repositoryPath,
        ProjectStorageService storage)
    {
        var root = Path.GetFullPath(repositoryPath);
        var plans = new List<RepositorySourceWritePlan>();
        var conflicts = new List<RepositorySourceConflict>();
        var warnings = new List<string>();

        foreach (var file in project.Folders.SelectMany(folder => folder.Files))
        {
            if (file.IsLegacyUnmapped || string.IsNullOrWhiteSpace(file.RepositoryRelativePath))
            {
                if (file.IsLegacyUnmapped)
                    warnings.Add($"{file.Name} is an unmapped legacy file; its old source remains in the legacy project backup.");
                continue;
            }

            var relativePath = NormalizeRelativePath(file.RepositoryRelativePath);
            if (!relativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                conflicts.Add(new RepositorySourceConflict(relativePath, "Code Loom only writes repository-backed .cs files."));
                continue;
            }

            string fullPath;
            try
            {
                fullPath = ResolveRepositoryPath(root, relativePath);
            }
            catch (Exception exception)
            {
                conflicts.Add(new RepositorySourceConflict(relativePath, exception.Message));
                continue;
            }

            var assembled = CodeAssembler.Assemble(file);
            var assembledHash = HashText(assembled);
            var exists = File.Exists(fullPath);
            var diskHash = exists ? RepositoryCSharpScanner.HashFile(fullPath) : string.Empty;

            if (exists)
            {
                if (!string.IsNullOrWhiteSpace(file.SourceHash)
                    && !string.Equals(diskHash, file.SourceHash, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(diskHash, assembledHash, StringComparison.OrdinalIgnoreCase))
                {
                    conflicts.Add(new RepositorySourceConflict(
                        relativePath,
                        "The physical .cs file changed outside Code Loom after it was loaded. The external edit was preserved."));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(file.SourceHash)
                    && !string.Equals(diskHash, assembledHash, StringComparison.OrdinalIgnoreCase))
                {
                    conflicts.Add(new RepositorySourceConflict(
                        relativePath,
                        "A different .cs file already exists at this new Code Loom path. It was not overwritten."));
                    continue;
                }
            }
            else if (!string.IsNullOrWhiteSpace(file.SourceHash))
            {
                conflicts.Add(new RepositorySourceConflict(
                    relativePath,
                    "The physical .cs file was removed outside Code Loom after it was loaded. Code Loom did not recreate it silently."));
                continue;
            }

            plans.Add(new RepositorySourceWritePlan(file, relativePath, fullPath, assembled, assembledHash, diskHash));
        }

        if (conflicts.Count > 0)
            return RepositoryProjectSaveResult.Fail(conflicts, warnings);

        var written = 0;
        var unchanged = 0;
        foreach (var plan in plans)
        {
            if (string.Equals(plan.DiskHash, plan.AssembledHash, StringComparison.OrdinalIgnoreCase))
            {
                unchanged++;
            }
            else
            {
                AtomicWrite(plan.FullPath, plan.Content);
                written++;
            }

            plan.File.RepositoryRelativePath = plan.RelativePath;
            plan.File.SourceHash = plan.AssembledHash;
            plan.File.IsRepositoryBacked = true;
        }

        storage.SaveRepositoryMetadata(project, root);
        return new RepositoryProjectSaveResult(true, written, unchanged, conflicts, warnings);
    }

    public Dictionary<string, string> CreateProjectSnapshot(CodeProject project)
    {
        return project.Folders
            .SelectMany(folder => folder.Files)
            .Where(file => !string.IsNullOrWhiteSpace(file.RepositoryRelativePath)
                           && !string.IsNullOrWhiteSpace(file.SourceHash))
            .GroupBy(file => NormalizeRelativePath(file.RepositoryRelativePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().SourceHash, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> GetLocallyModifiedSourcePaths(CodeProject project)
    {
        return project.Folders
            .SelectMany(folder => folder.Files)
            .Where(file => !file.IsLegacyUnmapped && !string.IsNullOrWhiteSpace(file.RepositoryRelativePath))
            .Where(file => !string.Equals(
                HashText(CodeAssembler.Assemble(file)),
                file.SourceHash,
                StringComparison.OrdinalIgnoreCase))
            .Select(file => NormalizeRelativePath(file.RepositoryRelativePath))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string NormalizeRelativePath(string value)
    {
        return (value ?? string.Empty).Replace('\\', '/').TrimStart('/');
    }

    private static void AddToPhysicalFolder(CodeProject project, CodeFile file)
    {
        var directory = Path.GetDirectoryName(file.RepositoryRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var normalizedDirectory = string.IsNullOrWhiteSpace(directory)
            ? "(repository root)"
            : NormalizeRelativePath(directory);

        var folder = project.Folders.FirstOrDefault(candidate =>
            string.Equals(candidate.RepositoryRelativePath, normalizedDirectory, StringComparison.OrdinalIgnoreCase));
        if (folder is null)
        {
            folder = new CodeFolder
            {
                Name = normalizedDirectory,
                RepositoryRelativePath = normalizedDirectory == "(repository root)" ? string.Empty : normalizedDirectory
            };
            project.Folders.Add(folder);
        }

        folder.Files.Add(file);
    }

    private static void ApplyMetadata(CodeFile file, CodeLoomFileMetadata metadata)
    {
        var subfiles = metadata.Subfiles.ToDictionary(subfile => subfile.Name, StringComparer.Ordinal);
        foreach (var subfile in file.Subfiles)
        {
            if (!subfiles.TryGetValue(subfile.Name, out var saved))
                continue;

            subfile.Role = saved.Role;
            subfile.AssemblySection = saved.AssemblySection;
            subfile.Receives = saved.Receives;
            subfile.Returns = saved.Returns;
            subfile.UsedBy = saved.UsedBy;
            subfile.Purpose = saved.Purpose;
        }

        foreach (var variable in file.Variables)
        {
            if (metadata.VariableMeanings.TryGetValue(variable.Name, out var meaning))
                variable.Meaning = meaning;
        }
    }

    private static void ApplyLegacyMetadata(CodeFile target, CodeFile legacy)
    {
        var legacySubfiles = legacy.Subfiles.ToDictionary(subfile => subfile.Name, StringComparer.Ordinal);
        foreach (var subfile in target.Subfiles)
        {
            if (!legacySubfiles.TryGetValue(subfile.Name, out var saved))
                continue;

            subfile.Role = saved.Role;
            subfile.AssemblySection = saved.AssemblySection;
            subfile.Receives = saved.Receives;
            subfile.Returns = saved.Returns;
            subfile.UsedBy = saved.UsedBy;
            subfile.Purpose = saved.Purpose;
        }

        var meanings = legacy.Variables
            .Where(variable => !string.IsNullOrWhiteSpace(variable.Meaning))
            .GroupBy(variable => variable.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Meaning, StringComparer.Ordinal);
        foreach (var variable in target.Variables)
        {
            if (meanings.TryGetValue(variable.Name, out var meaning))
                variable.Meaning = meaning;
        }
    }

    private static void SortProject(CodeProject project)
    {
        var sortedFolders = project.Folders
            .OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        project.Folders.Clear();
        foreach (var folder in sortedFolders)
        {
            var sortedFiles = folder.Files
                .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            folder.Files.Clear();
            foreach (var file in sortedFiles)
                folder.Files.Add(file);
            project.Folders.Add(folder);
        }
    }

    private static string ResolveRepositoryPath(string repositoryRoot, string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var candidate = Path.GetFullPath(Path.Combine(
            repositoryRoot,
            normalized.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = repositoryRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The repository-relative path points outside the selected repository.");

        return candidate;
    }

    private static string HashText(string content)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    private static void AtomicWrite(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath = path + ".codeloom.tmp";
        try
        {
            File.WriteAllText(temporaryPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
            }
        }
    }
}

public sealed record RepositoryProjectLoadResult(
    CodeProject Project,
    RepositoryCSharpScanResult Scan,
    IReadOnlyList<string> Warnings,
    bool MigratedLegacyProject);

public sealed record RepositorySourceConflict(string RelativePath, string Reason);

public sealed record RepositoryProjectSaveResult(
    bool Success,
    int WrittenCount,
    int UnchangedCount,
    IReadOnlyList<RepositorySourceConflict> Conflicts,
    IReadOnlyList<string> Warnings)
{
    public static RepositoryProjectSaveResult Fail(
        IReadOnlyList<RepositorySourceConflict> conflicts,
        IReadOnlyList<string> warnings)
    {
        return new RepositoryProjectSaveResult(false, 0, 0, conflicts, warnings);
    }
}

internal sealed record RepositorySourceWritePlan(
    CodeFile File,
    string RelativePath,
    string FullPath,
    string Content,
    string AssembledHash,
    string DiskHash);
