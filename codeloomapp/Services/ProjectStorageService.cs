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
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    public string GetProjectFilePath(string repositoryPath)
    {
        return Path.Combine(repositoryPath, ".codeloom", "project.json");
    }

    public void SaveProject(CodeProject project, string repositoryPath)
    {
        var projectFile = GetProjectFilePath(repositoryPath);
        var directory = Path.GetDirectoryName(projectFile)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(projectFile, JsonSerializer.Serialize(project, JsonOptions));
    }

    public CodeProject? LoadProject(string repositoryPath)
    {
        var projectFile = GetProjectFilePath(repositoryPath);
        if (!File.Exists(projectFile))
            return null;

        var json = File.ReadAllText(projectFile);
        return JsonSerializer.Deserialize<CodeProject>(json, JsonOptions);
    }
}

public sealed class AppSettings
{
    public string GitRepositoryPath { get; set; } = string.Empty;
}
