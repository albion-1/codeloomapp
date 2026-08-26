using System.IO;
using System.Text.Json;
using codeloomapp.Models;

namespace codeloomapp.Services;

public sealed class ProjectStorageService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodeLoom");

    private string SettingsPath => Path.Combine(_settingsDirectory, "settings.json");
    private string RecoveryPath => Path.Combine(_settingsDirectory, "recovery.json");

    public AppSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        Directory.CreateDirectory(_settingsDirectory);
        AtomicWrite(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    public string GetProjectFilePath(string repositoryPath)
    {
        return Path.Combine(repositoryPath, ".codeloom", "project.json");
    }

    public string GetLegacyBackupFilePath(string repositoryPath)
    {
        return Path.Combine(repositoryPath, ".codeloom", "project.legacy-backup.json");
    }

    // Full project serialization is intentionally retained for local recovery/history.
    // project.json itself is written through SaveRepositoryMetadata and never stores code.
    public string SerializeProject(CodeProject project)
    {
        return JsonSerializer.Serialize(project, JsonOptions);
    }

    public CodeProject? DeserializeProject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        return JsonSerializer.Deserialize<CodeProject>(json, JsonOptions);
    }

    public RepositoryMetadataLoadResult LoadRepositoryMetadata(string repositoryPath)
    {
        var projectFile = GetProjectFilePath(repositoryPath);
        if (!File.Exists(projectFile))
            return new RepositoryMetadataLoadResult();

        var json = File.ReadAllText(projectFile);
        if (LooksLikeMetadataV2(json))
        {
            return new RepositoryMetadataLoadResult
            {
                Metadata = JsonSerializer.Deserialize<CodeLoomProjectMetadata>(json, JsonOptions)
                           ?? new CodeLoomProjectMetadata()
            };
        }

        var legacy = DeserializeProject(json);
        return new RepositoryMetadataLoadResult
        {
            LegacyProject = legacy,
            IsLegacyProject = legacy is not null
        };
    }

    public void SaveRepositoryMetadata(CodeProject project, string repositoryPath)
    {
        var projectFile = GetProjectFilePath(repositoryPath);
        BackupLegacyProjectIfNeeded(repositoryPath);

        var metadata = new CodeLoomProjectMetadata
        {
            SchemaVersion = 2,
            Name = project.Name,
            Files = project.Folders
                .SelectMany(folder => folder.Files)
                .Where(file => !string.IsNullOrWhiteSpace(file.RepositoryRelativePath))
                .Select(file => new CodeLoomFileMetadata
                {
                    RelativePath = NormalizeRelativePath(file.RepositoryRelativePath),
                    LastKnownHash = file.SourceHash,
                    Subfiles = file.Subfiles.Select(subfile => new CodeLoomSubfileMetadata
                    {
                        Name = subfile.Name,
                        Role = subfile.Role,
                        AssemblySection = subfile.AssemblySection,
                        Receives = subfile.Receives,
                        Returns = subfile.Returns,
                        UsedBy = subfile.UsedBy,
                        Purpose = subfile.Purpose
                    }).ToList(),
                    VariableMeanings = file.Variables
                        .Where(variable => !string.IsNullOrWhiteSpace(variable.Name)
                                           && !string.IsNullOrWhiteSpace(variable.Meaning))
                        .GroupBy(variable => variable.Name, StringComparer.Ordinal)
                        .ToDictionary(group => group.Key, group => group.Last().Meaning, StringComparer.Ordinal)
                })
                .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };

        var directory = Path.GetDirectoryName(projectFile)!;
        Directory.CreateDirectory(directory);
        AtomicWrite(projectFile, JsonSerializer.Serialize(metadata, JsonOptions));
    }

    // Compatibility aliases. Repository project callers should prefer the explicit
    // metadata methods; these aliases make any older call site safe because they no
    // longer serialize C# source into project.json.
    public void SaveProject(CodeProject project, string repositoryPath)
    {
        SaveRepositoryMetadata(project, repositoryPath);
    }

    public CodeProject? LoadProject(string repositoryPath)
    {
        var loaded = LoadRepositoryMetadata(repositoryPath);
        if (loaded.LegacyProject is not null)
            return loaded.LegacyProject;
        if (loaded.Metadata is null)
            return null;

        return new CodeProject
        {
            SchemaVersion = loaded.Metadata.SchemaVersion,
            Name = string.IsNullOrWhiteSpace(loaded.Metadata.Name)
                ? Path.GetFileName(Path.GetFullPath(repositoryPath).TrimEnd(Path.DirectorySeparatorChar))
                : loaded.Metadata.Name
        };
    }

    public DateTime? GetProjectLastWriteTimeUtc(string repositoryPath)
    {
        var projectFile = GetProjectFilePath(repositoryPath);
        return File.Exists(projectFile)
            ? File.GetLastWriteTimeUtc(projectFile)
            : null;
    }

    public void SaveRecoverySnapshot(RecoverySnapshot snapshot)
    {
        Directory.CreateDirectory(_settingsDirectory);
        AtomicWrite(RecoveryPath, JsonSerializer.Serialize(snapshot, JsonOptions));
    }

    public RecoverySnapshot? LoadRecoverySnapshot()
    {
        try
        {
            if (!File.Exists(RecoveryPath))
                return null;

            var json = File.ReadAllText(RecoveryPath);
            return JsonSerializer.Deserialize<RecoverySnapshot>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public void DeleteRecoverySnapshot()
    {
        try
        {
            if (File.Exists(RecoveryPath))
                File.Delete(RecoveryPath);
        }
        catch
        {
            // Recovery cleanup is best-effort and must never block the app.
        }
    }

    private void BackupLegacyProjectIfNeeded(string repositoryPath)
    {
        var projectFile = GetProjectFilePath(repositoryPath);
        if (!File.Exists(projectFile))
            return;

        try
        {
            var json = File.ReadAllText(projectFile);
            if (LooksLikeMetadataV2(json))
                return;

            var backupPath = GetLegacyBackupFilePath(repositoryPath);
            if (!File.Exists(backupPath))
                File.Copy(projectFile, backupPath, overwrite: false);
        }
        catch
        {
            // If a backup cannot be created, do not transform an older project file.
            throw new IOException(
                "Code Loom found an older project.json but could not create its migration backup. " +
                "The existing project was left untouched.");
        }
    }

    private static bool LooksLikeMetadataV2(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.TryGetProperty("SchemaVersion", out var version)
                   && version.TryGetInt32(out var value)
                   && value >= 2
                   && root.TryGetProperty("Files", out _);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeRelativePath(string value)
    {
        return (value ?? string.Empty).Replace('\\', '/').TrimStart('/');
    }

    private static void AtomicWrite(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath = path + ".tmp";

        try
        {
            File.WriteAllText(temporaryPath, content);
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
                // A stale temp file is harmless; the next atomic write replaces it.
            }
        }
    }
}

public sealed class AppSettings
{
    public string GitRepositoryPath { get; set; } = string.Empty;
    public Dictionary<string, string> UnityProjectPaths { get; set; } = new();

    // Lightweight workstation preferences. These are intentionally global rather
    // than project data so opening another project keeps the editor comfortable.
    public double EditorFontSize { get; set; } = 13;
    public bool EditorWordWrap { get; set; }
    public bool ShowEditorLineNumbers { get; set; } = true;
    public int AutosaveSeconds { get; set; } = 3;
}

public sealed class RecoverySnapshot
{
    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;
    public string RepositoryPath { get; set; } = string.Empty;
    public bool CleanShutdown { get; set; }
    public CodeProject Project { get; set; } = new();
}
