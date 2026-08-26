using System.Collections.ObjectModel;

namespace codeloomapp.Models;

public sealed class CodeProject
{
    public int SchemaVersion { get; set; } = 2;
    public string Name { get; set; } = "Untitled Project";
    public ObservableCollection<CodeFolder> Folders { get; set; } = new();
}

public sealed class CodeFolder
{
    public string Name { get; set; } = string.Empty;
    public string RepositoryRelativePath { get; set; } = string.Empty;
    public ObservableCollection<CodeFile> Files { get; set; } = new();
}

public sealed class CodeFile
{
    public string Name { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public string BaseClass { get; set; } = "MonoBehaviour";

    // Physical repository identity. SourceHash describes the actual .cs bytes that
    // were loaded. ProjectionHash describes the Code Loom assembly at that moment.
    // Keeping them separate prevents a no-op Save from reformatting imported source.
    public string RepositoryRelativePath { get; set; } = string.Empty;
    public string SourceHash { get; set; } = string.Empty;
    public string ProjectionHash { get; set; } = string.Empty;
    public bool IsRepositoryBacked { get; set; }
    public bool IsLegacyUnmapped { get; set; }

    // These properties let imported source keep important type-level structure
    // without making the normal Code Loom workflow more complicated.
    public string Namespace { get; set; } = string.Empty;
    public string TypeKind { get; set; } = "class";
    public string TypeModifiers { get; set; } = "public";
    public string TypeAttributes { get; set; } = string.Empty;

    public ObservableCollection<string> UsingStatements { get; set; } = new();
    public ObservableCollection<CodeSubfile> Subfiles { get; set; } = new();
    public ObservableCollection<VariableDefinition> Variables { get; set; } = new();

    public override string ToString() => Name;
}

public static class AssemblySections
{
    public const string Auto = "Auto";
    public const string Fields = "Fields & Settings";
    public const string Properties = "Properties";
    public const string Constructors = "Constructors";
    public const string UnityLifecycle = "Unity Lifecycle";
    public const string Methods = "Methods";
    public const string NestedTypes = "Nested Types";
    public const string Other = "Other";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        Auto,
        Fields,
        Properties,
        Constructors,
        UnityLifecycle,
        Methods,
        NestedTypes,
        Other
    };
}

public sealed class CodeSubfile
{
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;

    // Auto lets Code Loom inspect the code fragment and choose where it belongs
    // in the assembled class. A manual section overrides that inference.
    public string AssemblySection { get; set; } = AssemblySections.Auto;

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

    // These fields make the Variables view a projection of the actual C# source
    // instead of a separate documentation-only list.
    public bool IsCodeBacked { get; set; }
    public string Access { get; set; } = string.Empty;
    public string Modifiers { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public int SourceLine { get; set; }
}
