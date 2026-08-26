using codeloomapp.Models;
using codeloomapp.Services;

namespace codeloomapp;

public partial class MainWindow
{
    private static void MergeLocalCodeLoomMetadata(CodeProject source, CodeProject target)
    {
        var sourceFiles = source.Folders
            .SelectMany(folder => folder.Files)
            .Where(file => !string.IsNullOrWhiteSpace(file.RepositoryRelativePath))
            .GroupBy(
                file => RepositoryProjectService.NormalizeRelativePath(file.RepositoryRelativePath),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        foreach (var targetFile in target.Folders.SelectMany(folder => folder.Files))
        {
            var path = RepositoryProjectService.NormalizeRelativePath(targetFile.RepositoryRelativePath);
            if (string.IsNullOrWhiteSpace(path) || !sourceFiles.TryGetValue(path, out var sourceFile))
                continue;

            var sourceSubfiles = sourceFile.Subfiles
                .GroupBy(subfile => subfile.Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

            foreach (var targetSubfile in targetFile.Subfiles)
            {
                if (!sourceSubfiles.TryGetValue(targetSubfile.Name, out var sourceSubfile))
                    continue;

                targetSubfile.Role = sourceSubfile.Role;
                targetSubfile.AssemblySection = sourceSubfile.AssemblySection;
                targetSubfile.Receives = sourceSubfile.Receives;
                targetSubfile.Returns = sourceSubfile.Returns;
                targetSubfile.UsedBy = sourceSubfile.UsedBy;
                targetSubfile.Purpose = sourceSubfile.Purpose;
            }

            var variableMeanings = sourceFile.Variables
                .Where(variable => !string.IsNullOrWhiteSpace(variable.Name)
                                   && !string.IsNullOrWhiteSpace(variable.Meaning))
                .GroupBy(variable => variable.Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last().Meaning, StringComparer.Ordinal);

            foreach (var targetVariable in targetFile.Variables)
            {
                if (variableMeanings.TryGetValue(targetVariable.Name, out var meaning))
                    targetVariable.Meaning = meaning;
            }
        }
    }
}
