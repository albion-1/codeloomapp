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

    public string GetProjectBackupFilePath(string repositoryPath)
    {
        return Path.Combine(repositoryPath, ".codeloom", "project.backup.json");
    }

    public string SerializeProject(CodeProject project)
    {
        return JsonSerializer.Serialize(project, JsonOptions);
    }

    public void SaveProject(CodeProject project, string repositoryPath)
    {
        var projectFile = GetProjectFilePath(repositoryPath);
        var backupFile = GetProjectBackupFilePath(repositoryPath);
        var directory = Path.GetDirectoryName(projectFile)!;
        Directory.CreateDirectory(directory);

        AtomicWrite(projectFile, SerializeProject(project), backupFile);
    }

    public CodeProject? LoadProject(string repositoryPath)
    {
        var projectFile = GetProjectFilePath(repositoryPath);
        var backupFile = GetProjectBackupFilePath(repositoryPath);

        if (!File.Exists(projectFile))
            return null;

        try
        {
            return DeserializeProject(File.ReadAllText(projectFile));
        }
        catch when (File.Exists(backupFile))
        {
            // If a write was interrupted or the main file became malformed,
            // fall back to the last known-good project copy.
            return DeserializeProject(File.ReadAllText(backupFile));
        }
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

    private static CodeProject? DeserializeProject(string json)
    {
        return JsonSerializer.Deserialize<CodeProject>(json, JsonOptions);
    }

    private static void AtomicWrite(string path, string content, string? backupPath = null)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath = path + ".tmp";

        try
        {
            File.WriteAllText(temporaryPath, content);

            if (backupPath is not null && File.Exists(path))
                File.Copy(path, backupPath, overwrite: true);

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
}

public sealed class RecoverySnapshot
{
    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;
    public string RepositoryPath { get; set; } = string.Empty;
    public bool CleanShutdown { get; set; }
    public CodeProject Project { get; set; } = new();
}
