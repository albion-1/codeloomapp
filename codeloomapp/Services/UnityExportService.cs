using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using codeloomapp.Models;

namespace codeloomapp.Services;

public sealed class UnityExportService
{
    public const string GeneratedRelativePath = "Assets/CodeLoom/Generated";

    private const string ManifestRelativePath = ".codeloom/unity-export.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public bool IsUnityProject(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return false;

        return Directory.Exists(Path.Combine(path, "Assets"))
               && Directory.Exists(Path.Combine(path, "ProjectSettings"));
    }

    public string? NormalizeUnityProjectPath(string? selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
            return null;

        var fullPath = Path.GetFullPath(selectedPath);
        if (IsUnityProject(fullPath))
            return fullPath;

        var directoryName = Path.GetFileName(
            fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(directoryName, "Assets", StringComparison.OrdinalIgnoreCase)
            || string.Equals(directoryName, "ProjectSettings", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(fullPath)?.FullName;
            if (IsUnityProject(parent))
                return parent;
        }

        return null;
    }

    public string GetGeneratedRoot(string unityProjectPath)
    {
        return Path.Combine(
            unityProjectPath,
            "Assets",
            "CodeLoom",
            "Generated");
    }

    public UnityExportResult Export(CodeProject project, string unityProjectPath)
    {
        var normalizedProjectPath = NormalizeUnityProjectPath(unityProjectPath);
        if (normalizedProjectPath is null)
        {
            return UnityExportResult.Fail(
                "That folder is not a Unity project. Choose the project root that contains both Assets and ProjectSettings.");
        }

        try
        {
            var generatedRoot = GetGeneratedRoot(normalizedProjectPath);
            var manifestPath = Path.Combine(normalizedProjectPath, ".codeloom", "unity-export.json");
            Directory.CreateDirectory(generatedRoot);

            var manifest = LoadManifest(manifestPath, out var manifestWarning);
            var previousEntries = new Dictionary<string, UnityExportManifestEntry>(
                manifest.Files,
                StringComparer.OrdinalIgnoreCase);

            var plan = BuildPlan(project);
            if (plan.DuplicatePaths.Count > 0)
            {
                return UnityExportResult.Fail(
                    "Two Code Loom files would export to the same Unity path after Windows-safe name cleanup:\n\n" +
                    string.Join("\n", plan.DuplicatePaths.Select(path => "• " + path)));
            }

            var conflicts = new List<UnityExportConflict>();
            var warnings = new List<string>();
            if (!string.IsNullOrWhiteSpace(manifestWarning))
                warnings.Add(manifestWarning);
            warnings.AddRange(plan.Warnings);

            var nextEntries = new Dictionary<string, UnityExportManifestEntry>(StringComparer.OrdinalIgnoreCase);
            var exportedCount = 0;
            var unchangedCount = 0;
            var deletedCount = 0;

            foreach (var item in plan.Files)
            {
                var fullPath = CombineRelative(normalizedProjectPath, item.RelativePath);
                var currentDiskHash = File.Exists(fullPath)
                    ? HashFile(fullPath)
                    : string.Empty;

                if (previousEntries.TryGetValue(item.RelativePath, out var previous))
                {
                    var diskMatchesLastExport = !File.Exists(fullPath)
                                                || string.Equals(
                                                    currentDiskHash,
                                                    previous.Sha256,
                                                    StringComparison.OrdinalIgnoreCase);
                    var diskAlreadyMatchesNewExport = File.Exists(fullPath)
                                                     && string.Equals(
                                                         currentDiskHash,
                                                         item.Sha256,
                                                         StringComparison.OrdinalIgnoreCase);

                    if (!diskMatchesLastExport && !diskAlreadyMatchesNewExport)
                    {
                        conflicts.Add(new UnityExportConflict(
                            item.RelativePath,
                            "Unity-side file changed since Code Loom last exported it. The external edit was preserved."));
                        nextEntries[item.RelativePath] = previous;
                        continue;
                    }

                    if (diskAlreadyMatchesNewExport)
                    {
                        unchangedCount++;
                        nextEntries[item.RelativePath] = new UnityExportManifestEntry(item.Sha256);
                        continue;
                    }
                }
                else if (File.Exists(fullPath))
                {
                    if (string.Equals(currentDiskHash, item.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        unchangedCount++;
                        nextEntries[item.RelativePath] = new UnityExportManifestEntry(item.Sha256);
                        continue;
                    }

                    conflicts.Add(new UnityExportConflict(
                        item.RelativePath,
                        "A file already exists at this generated path, but Code Loom has never exported it. It was left untouched."));
                    continue;
                }

                AtomicWrite(fullPath, item.Content);
                exportedCount++;
                nextEntries[item.RelativePath] = new UnityExportManifestEntry(item.Sha256);
            }

            foreach (var previous in previousEntries)
            {
                if (plan.ByRelativePath.ContainsKey(previous.Key))
                    continue;

                var fullPath = CombineRelative(normalizedProjectPath, previous.Key);
                if (!File.Exists(fullPath))
                    continue;

                var currentDiskHash = HashFile(fullPath);
                if (!string.Equals(
                        currentDiskHash,
                        previous.Value.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    conflicts.Add(new UnityExportConflict(
                        previous.Key,
                        "This generated file no longer exists in Code Loom, but it was edited after the last export. Code Loom kept it instead of deleting it."));
                    nextEntries[previous.Key] = previous.Value;
                    continue;
                }

                File.Delete(fullPath);
                var metaPath = fullPath + ".meta";
                if (File.Exists(metaPath))
                    File.Delete(metaPath);
                deletedCount++;
            }

            RemoveEmptyGeneratedDirectories(generatedRoot);

            manifest = new UnityExportManifest
            {
                Version = 1,
                LastExportedAtUtc = DateTime.UtcNow,
                GeneratedRelativePath = GeneratedRelativePath,
                Files = nextEntries
            };
            AtomicWrite(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));

            return new UnityExportResult(
                true,
                normalizedProjectPath,
                generatedRoot,
                exportedCount,
                unchangedCount,
                deletedCount,
                conflicts,
                warnings,
                BuildResultMessage(exportedCount, unchangedCount, deletedCount, conflicts.Count));
        }
        catch (Exception exception)
        {
            return UnityExportResult.Fail(
                "Code Loom could not export to Unity.\n\n" + exception.Message);
        }
    }

    private static UnityExportPlan BuildPlan(CodeProject project)
    {
        var files = new List<UnityExportPlanItem>();
        var warnings = new List<string>();
        var duplicatePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byRelativePath = new Dictionary<string, UnityExportPlanItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in project.Folders)
        {
            var safeFolder = MakeSafePathPart(folder.Name, "Scripts");
            if (!string.Equals(safeFolder, folder.Name, StringComparison.Ordinal))
                warnings.Add($"Unity export cleaned folder name '{folder.Name}' to '{safeFolder}'.");

            foreach (var file in folder.Files)
            {
                var requestedFileName = EnsureCsExtension(file.Name);
                var safeFileName = MakeSafeFileName(requestedFileName);
                if (!string.Equals(safeFileName, requestedFileName, StringComparison.Ordinal))
                    warnings.Add($"Unity export cleaned file name '{requestedFileName}' to '{safeFileName}'.");

                var relativePath = NormalizeRelativePath(
                    Path.Combine(GeneratedRelativePath, safeFolder, safeFileName));

                var content = CodeAssembler.Assemble(file);
                var item = new UnityExportPlanItem(
                    relativePath,
                    content,
                    HashText(content));

                if (!byRelativePath.TryAdd(relativePath, item))
                    duplicatePaths.Add(relativePath);
                else
                    files.Add(item);

                var plainClassName = file.ClassName.Split('<')[0].Trim();
                var fileStem = Path.GetFileNameWithoutExtension(safeFileName);
                if (file.BaseClass.Contains("MonoBehaviour", StringComparison.Ordinal)
                    && !string.Equals(fileStem, plainClassName, StringComparison.Ordinal))
                {
                    warnings.Add(
                        $"{safeFileName}: Unity components are easiest to attach when the file name matches the MonoBehaviour class name '{plainClassName}'.");
                }
            }
        }

        return new UnityExportPlan(files, byRelativePath, duplicatePaths.ToList(), warnings);
    }

    private static UnityExportManifest LoadManifest(string manifestPath, out string warning)
    {
        warning = string.Empty;
        if (!File.Exists(manifestPath))
            return new UnityExportManifest();

        try
        {
            var json = File.ReadAllText(manifestPath);
            return JsonSerializer.Deserialize<UnityExportManifest>(json, JsonOptions)
                   ?? new UnityExportManifest();
        }
        catch
        {
            warning =
                "The previous Unity export manifest could not be read. Existing generated files will be treated conservatively and will not be overwritten unless they already match the new Code Loom output.";
            return new UnityExportManifest();
        }
    }

    private static string BuildResultMessage(
        int exportedCount,
        int unchangedCount,
        int deletedCount,
        int conflictCount)
    {
        var pieces = new List<string>();
        if (exportedCount > 0)
            pieces.Add(exportedCount == 1 ? "1 script written" : $"{exportedCount} scripts written");
        if (unchangedCount > 0)
            pieces.Add(unchangedCount == 1 ? "1 unchanged" : $"{unchangedCount} unchanged");
        if (deletedCount > 0)
            pieces.Add(deletedCount == 1 ? "1 stale script removed" : $"{deletedCount} stale scripts removed");
        if (conflictCount > 0)
            pieces.Add(conflictCount == 1 ? "1 external edit preserved" : $"{conflictCount} external edits preserved");

        return pieces.Count == 0
            ? "Unity export is already up to date."
            : "Unity export: " + string.Join(" · ", pieces) + ".";
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
                // A stale temporary export file is harmless and can be replaced next time.
            }
        }
    }

    private static string HashText(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    private static string EnsureCsExtension(string fileName)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? "UnnamedScript.cs" : fileName.Trim();
        return name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            ? name
            : name + ".cs";
    }

    private static string MakeSafeFileName(string value)
    {
        var stem = Path.GetFileNameWithoutExtension(value);
        return MakeSafePathPart(stem, "UnnamedScript") + ".cs";
    }

    private static string MakeSafePathPart(string value, string fallback)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((value ?? string.Empty)
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray())
            .Trim()
            .TrimEnd('.', ' ');

        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }

    private static string CombineRelative(string root, string relativePath)
    {
        var parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Aggregate(root, Path.Combine);
    }

    private static void RemoveEmptyGeneratedDirectories(string generatedRoot)
    {
        if (!Directory.Exists(generatedRoot))
            return;

        foreach (var directory in Directory
                     .EnumerateDirectories(generatedRoot, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
            catch
            {
                // Folder cleanup is cosmetic; never fail an otherwise successful export.
            }
        }
    }
}

public sealed class UnityExportManifest
{
    public int Version { get; set; } = 1;
    public DateTime LastExportedAtUtc { get; set; } = DateTime.UtcNow;
    public string GeneratedRelativePath { get; set; } = UnityExportService.GeneratedRelativePath;
    public Dictionary<string, UnityExportManifestEntry> Files { get; set; } = new();
}

public sealed class UnityExportManifestEntry
{
    public UnityExportManifestEntry()
    {
    }

    public UnityExportManifestEntry(string sha256)
    {
        Sha256 = sha256;
    }

    public string Sha256 { get; set; } = string.Empty;
}

public sealed record UnityExportConflict(string RelativePath, string Reason);

public sealed record UnityExportResult(
    bool Success,
    string UnityProjectPath,
    string GeneratedRoot,
    int ExportedCount,
    int UnchangedCount,
    int DeletedCount,
    IReadOnlyList<UnityExportConflict> Conflicts,
    IReadOnlyList<string> Warnings,
    string Message)
{
    public static UnityExportResult Fail(string message)
    {
        return new UnityExportResult(
            false,
            string.Empty,
            string.Empty,
            0,
            0,
            0,
            Array.Empty<UnityExportConflict>(),
            Array.Empty<string>(),
            message);
    }
}

internal sealed record UnityExportPlanItem(
    string RelativePath,
    string Content,
    string Sha256);

internal sealed record UnityExportPlan(
    IReadOnlyList<UnityExportPlanItem> Files,
    IReadOnlyDictionary<string, UnityExportPlanItem> ByRelativePath,
    IReadOnlyList<string> DuplicatePaths,
    IReadOnlyList<string> Warnings);
