using System.Collections.ObjectModel;

namespace codeloomapp.Models;

public sealed class CodeLoomProjectMetadata
{
    public int SchemaVersion { get; set; } = 2;
    public string Name { get; set; } = string.Empty;
    public List<CodeLoomFileMetadata> Files { get; set; } = new();
}

public sealed class CodeLoomFileMetadata
{
    public string RelativePath { get; set; } = string.Empty;
    public string LastKnownHash { get; set; } = string.Empty;
    public List<CodeLoomSubfileMetadata> Subfiles { get; set; } = new();
    public Dictionary<string, string> VariableMeanings { get; set; } = new(StringComparer.Ordinal);
}

public sealed class CodeLoomSubfileMetadata
{
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string AssemblySection { get; set; } = AssemblySections.Auto;
    public string Receives { get; set; } = "Nothing";
    public string Returns { get; set; } = "Nothing";
    public string UsedBy { get; set; } = "Not specified";
    public string Purpose { get; set; } = string.Empty;
}

public sealed class RepositoryMetadataLoadResult
{
    public CodeLoomProjectMetadata? Metadata { get; init; }
    public CodeProject? LegacyProject { get; init; }
    public bool IsLegacyProject { get; init; }
}
