using codeloomapp.Models;

namespace codeloomapp.Services;

public sealed class RepositoryAwareUnityExportService
{
    private readonly UnityExportService _exporter;

    public RepositoryAwareUnityExportService(UnityExportService exporter)
    {
        _exporter = exporter;
    }

    public UnityExportResult Export(
        CodeProject project,
        string unityProjectPath,
        string? sourceRepositoryPath)
    {
        var normalizedUnity = _exporter.NormalizeUnityProjectPath(unityProjectPath);
        if (normalizedUnity is null)
            return _exporter.Export(project, unityProjectPath);

        var sameProject = PathsEqual(normalizedUnity, sourceRepositoryPath);
        if (!sameProject)
            return _exporter.Export(project, normalizedUnity);

        var filtered = new CodeProject
        {
            SchemaVersion = project.SchemaVersion,
            Name = project.Name
        };
        var skipped = new List<string>();

        foreach (var sourceFolder in project.Folders)
        {
            var targetFolder = new CodeFolder
            {
                Name = sourceFolder.Name,
                RepositoryRelativePath = sourceFolder.RepositoryRelativePath
            };

            foreach (var file in sourceFolder.Files)
            {
                var relativePath = RepositoryProjectService.NormalizeRelativePath(file.RepositoryRelativePath);
                if (file.IsRepositoryBacked
                    && (relativePath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(relativePath, "Assets", StringComparison.OrdinalIgnoreCase)))
                {
                    skipped.Add(relativePath);
                    continue;
                }

                targetFolder.Files.Add(file);
            }

            if (targetFolder.Files.Count > 0)
                filtered.Folders.Add(targetFolder);
        }

        var result = _exporter.Export(filtered, normalizedUnity);
        if (!result.Success || skipped.Count == 0)
            return result;

        var warnings = result.Warnings.ToList();
        warnings.Add(
            skipped.Count == 1
                ? $"{skipped[0]} already lives inside this Unity project's Assets folder, so Code Loom did not create a duplicate generated copy."
                : $"{skipped.Count} repository-backed scripts already live inside this Unity project's Assets folder, so Code Loom did not duplicate them under Assets/CodeLoom/Generated.");

        return result with
        {
            Warnings = warnings,
            Message = result.Message + $" {skipped.Count} source script(s) already live in Unity Assets and were not duplicated."
        };
    }

    private static bool PathsEqual(string first, string? second)
    {
        if (string.IsNullOrWhiteSpace(second))
            return false;

        try
        {
            var firstFull = Path.GetFullPath(first)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var secondFull = Path.GetFullPath(second)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(firstFull, secondFull, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
