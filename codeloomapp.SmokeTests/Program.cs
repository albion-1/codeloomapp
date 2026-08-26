using codeloomapp.Models;
using codeloomapp.Services;

var tests = new List<(string Name, Action Run)>
{
    ("C# and Windows name safety", TestNameSafety),
    ("Smart assembly preserves comment markers in strings", TestSmartAssemblyStringSafety),
    ("Variable sync rejects ambiguous and invalid declarations", TestVariableSyncSafety),
    ("Existing C# imports and reassembles", TestImportRoundTrip),
    ("Project JSON round-trips", TestProjectStorageRoundTrip),
    ("Unity export preserves external edits", TestUnityExportConflictProtection)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{test.Name}: {exception.Message}");
        Console.Error.WriteLine($"FAIL  {test.Name}\n      {exception}");
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"{failures.Count} smoke test(s) failed.");
    Environment.Exit(1);
}

Console.WriteLine();
Console.WriteLine($"All {tests.Count} Code Loom smoke tests passed.");
return;

static void TestNameSafety()
{
    Assert(NameSafetyService.IsValidCSharpIdentifier("PlayerMovement"), "normal C# identifier should be valid");
    Assert(!NameSafetyService.IsValidCSharpIdentifier("class"), "reserved C# keyword should be rejected");
    Assert(NameSafetyService.MakeSafeCSharpIdentifier("class") == "_class", "keyword cleanup should create a valid identifier");
    Assert(NameSafetyService.IsValidWindowsFileName("PlayerMovement.cs"), "normal C# file name should be valid");
    Assert(!NameSafetyService.IsValidWindowsFileName("CON.cs"), "reserved Windows device name should be rejected");
    Assert(!NameSafetyService.IsValidWindowsFileName("Bad/Name.cs"), "path separators should be rejected from file names");
}

static void TestSmartAssemblyStringSafety()
{
    var file = new CodeFile
    {
        Name = "EndpointConfig.cs",
        ClassName = "EndpointConfig",
        BaseClass = string.Empty
    };
    var subfile = new CodeSubfile
    {
        Name = "EndpointConfig.Settings",
        Role = "Settings",
        Code = "private string endpoint = \"https://example.com/api\"; // real comment"
    };
    file.Subfiles.Add(subfile);

    var classification = CodeAssembler.Classify(file, subfile);
    Assert(classification.Section == AssemblySections.Fields,
        $"URL string was misclassified as {classification.Section}");

    var assembled = CodeAssembler.Assemble(file);
    Assert(assembled.Contains("https://example.com/api", StringComparison.Ordinal),
        "assembly should preserve URL string content");
}

static void TestVariableSyncSafety()
{
    var file = new CodeFile { Name = "Stats.cs", ClassName = "Stats", BaseClass = string.Empty };
    var subfile = new CodeSubfile
    {
        Name = "Stats.Settings",
        Role = "Settings",
        Code = "private int x, y;\nprivate int health = 5;"
    };
    file.Subfiles.Add(subfile);

    VariableSyncService.SyncFromCode(file);
    Assert(file.Variables.Count == 1, "multi-variable declaration should be ignored instead of misread");
    Assert(file.Variables[0].Name == "health", "health should be the detected field");

    var updated = VariableSyncService.TryUpdateField(
        file,
        file.Variables[0],
        "class",
        "int",
        "5",
        out _,
        out _);
    Assert(!updated, "field rename to a C# keyword should be rejected");
}

static void TestImportRoundTrip()
{
    const string source = """
        using UnityEngine;

        namespace Sample.Game
        {
            public class Player : MonoBehaviour
            {
                [SerializeField]
                private float speed = 5f;

                private void Update()
                {
                    Move();
                }

                private void Move()
                {
                    transform.position += Vector3.forward * speed * Time.deltaTime;
                }
            }
        }
        """;

    var imported = CSharpImportService.Import(source, "Player.cs");
    Assert(imported.File.ClassName == "Player", "imported class name should be Player");
    Assert(imported.File.Namespace == "Sample.Game", "namespace should be preserved");
    Assert(imported.File.Subfiles.Count >= 3, "field and methods should become multiple conceptual subfiles");

    var assembled = CodeAssembler.Assemble(imported.File);
    Assert(assembled.Contains("namespace Sample.Game", StringComparison.Ordinal), "assembled file should retain namespace");
    Assert(assembled.Contains("public class Player : MonoBehaviour", StringComparison.Ordinal), "assembled file should retain type declaration");
    Assert(assembled.Contains("private void Update()", StringComparison.Ordinal), "assembled file should retain Update");
    Assert(assembled.Contains("private void Move()", StringComparison.Ordinal), "assembled file should retain Move");
}

static void TestProjectStorageRoundTrip()
{
    var project = new CodeProject { Name = "RoundTrip" };
    var folder = new CodeFolder { Name = "Player" };
    var file = new CodeFile
    {
        Name = "Mover.cs",
        Namespace = "RoundTrip.Player",
        ClassName = "Mover",
        BaseClass = "MonoBehaviour"
    };
    file.UsingStatements.Add("using UnityEngine;");
    file.Subfiles.Add(new CodeSubfile
    {
        Name = "Mover.Update",
        Role = "Unity lifecycle",
        Code = "private void Update() { }",
        Purpose = "Runs once per frame."
    });
    folder.Files.Add(file);
    project.Folders.Add(folder);

    var storage = new ProjectStorageService();
    var json = storage.SerializeProject(project);
    var restored = storage.DeserializeProject(json);

    Assert(restored is not null, "project should deserialize");
    Assert(restored!.Name == "RoundTrip", "project name should survive serialization");
    Assert(restored.Folders.Count == 1, "folder should survive serialization");
    Assert(restored.Folders[0].Files[0].Namespace == "RoundTrip.Player", "namespace should survive serialization");
    Assert(restored.Folders[0].Files[0].Subfiles[0].Purpose == "Runs once per frame.", "subfile metadata should survive serialization");
}

static void TestUnityExportConflictProtection()
{
    var temporaryRoot = Path.Combine(Path.GetTempPath(), "CodeLoomSmoke-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(temporaryRoot, "Assets"));
    Directory.CreateDirectory(Path.Combine(temporaryRoot, "ProjectSettings"));

    try
    {
        var project = new CodeProject { Name = "ExportSmoke" };
        var folder = new CodeFolder { Name = "Player" };
        var file = new CodeFile
        {
            Name = "TestMover.cs",
            ClassName = "TestMover",
            BaseClass = "MonoBehaviour"
        };
        file.UsingStatements.Add("using UnityEngine;");
        var behavior = new CodeSubfile
        {
            Name = "TestMover.Update",
            Role = "Unity lifecycle",
            Code = "private void Update()\n{\n}"
        };
        file.Subfiles.Add(behavior);
        folder.Files.Add(file);
        project.Folders.Add(folder);

        var exporter = new UnityExportService();
        var first = exporter.Export(project, temporaryRoot);
        Assert(first.Success, "first Unity export should succeed: " + first.Message);

        var generatedFile = Path.Combine(
            temporaryRoot,
            "Assets",
            "CodeLoom",
            "Generated",
            "Player",
            "TestMover.cs");
        Assert(File.Exists(generatedFile), "Unity export should create an ordinary .cs file");

        const string externalEdit = "// edited outside Code Loom\npublic class TestMover { }\n";
        File.WriteAllText(generatedFile, externalEdit);
        behavior.Code = "private void Update()\n{\n    Debug.Log(\"changed\");\n}";

        var second = exporter.Export(project, temporaryRoot);
        Assert(second.Success, "second Unity export should complete even with a preserved conflict");
        Assert(second.Conflicts.Count == 1, "external edit should be reported as exactly one conflict");
        Assert(File.ReadAllText(generatedFile) == externalEdit, "external edit must not be overwritten");
    }
    finally
    {
        try
        {
            if (Directory.Exists(temporaryRoot))
                Directory.Delete(temporaryRoot, recursive: true);
        }
        catch
        {
            // Temporary test cleanup should not hide a test result.
        }
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
