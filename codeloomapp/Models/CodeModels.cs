using System.Collections.ObjectModel;

namespace codeloomapp.Models;

public sealed class CodeProject
{
    public string Name { get; set; } = "Wizard Game";
    public ObservableCollection<CodeFolder> Folders { get; set; } = new();
}

public sealed class CodeFolder
{
    public string Name { get; set; } = string.Empty;
    public ObservableCollection<CodeFile> Files { get; set; } = new();
}

public sealed class CodeFile
{
    public string Name { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public string BaseClass { get; set; } = "MonoBehaviour";
    public ObservableCollection<string> UsingStatements { get; set; } = new();
    public ObservableCollection<CodeSubfile> Subfiles { get; set; } = new();
    public ObservableCollection<VariableDefinition> Variables { get; set; } = new();

    public override string ToString() => Name;
}

public sealed class CodeSubfile
{
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Receives { get; set; } = "Nothing";
    public string Returns { get; set; } = "Nothing";
    public string UsedBy { get; set; } = "Not specified";
    public string Purpose { get; set; } = string.Empty;

    public override string ToString() => Name;
}

public sealed class VariableDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string DefaultValue { get; set; } = string.Empty;
    public string DeclaredIn { get; set; } = string.Empty;
    public string Meaning { get; set; } = string.Empty;
}
