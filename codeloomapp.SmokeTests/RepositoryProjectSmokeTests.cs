using codeloomapp.Models;
using codeloomapp.Services;

internal static class RepositoryProjectSmokeTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeLoomRepoProjectSmoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Assets", "Scripts", "Player"));
        Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));

        var movementPath = Path.Combine(root, "Assets", "Scripts", "Player", "PlayerMovement.cs");
        var cameraPath = Path.Combine(root, "Assets", "Scripts", "Player", "PlayerCamera.cs");

        const string movementSource = """
            using UnityEngine;

            public class PlayerMovement : MonoBehaviour
            {
                [SerializeField] private float speed = 5f;

                private void Update()
                {
                    transform.position += Vector3.forward * speed * Time.deltaTime;
                }
            }
            """;

        const string cameraSource = """
            using UnityEngine;

            public class PlayerCamera : MonoBehaviour
            {
                [SerializeField] private float sensitivity = 2f;

                private void Update()
                {
                    Look();
                }

                private void Look()
                {
                    transform.Rotate(0f, sensitivity, 0f);
                }
            }
            """;

        File.WriteAllText(movementPath, movementSource);
        File.WriteAllText(cameraPath, cameraSource);

        try
        {
            var storage = new ProjectStorageService();
            var service = new RepositoryProjectService();
            var first = service.Load(root, new RepositoryMetadataLoadResult());

            var files = first.Project.Folders.SelectMany(folder => folder.Files).ToList();
            Assert(files.Count == 2, "repository project should contain two separately selectable physical C# files");

            var movement = files.Single(file => file.Name == "PlayerMovement.cs");
            var camera = files.Single(file => file.Name == "PlayerCamera.cs");
            Assert(movement.RepositoryRelativePath == "Assets/Scripts/Player/PlayerMovement.cs",
                "movement should preserve its repository-relative path");
            Assert(camera.RepositoryRelativePath == "Assets/Scripts/Player/PlayerCamera.cs",
                "camera should preserve its repository-relative path");
            Assert(movement.Subfiles.Count >= 2, "movement should be split into its own conceptual subfiles");
            Assert(camera.Subfiles.Count >= 3, "camera should be split into its own conceptual subfiles");

            // A no-op Save must not rewrite source merely because the assembler's format
            // differs from the physical file's original formatting.
            var firstSave = service.Save(first.Project, root, storage);
            Assert(firstSave.Success, "metadata-only initial save should succeed");
            Assert(File.ReadAllText(movementPath) == movementSource, "no-op save must preserve PlayerMovement.cs bytes");
            Assert(File.ReadAllText(cameraPath) == cameraSource, "no-op save must preserve PlayerCamera.cs bytes");

            var metadataJson = File.ReadAllText(storage.GetProjectFilePath(root));
            Assert(metadataJson.Contains("\"SchemaVersion\": 2", StringComparison.Ordinal),
                "project.json should use metadata schema version 2");
            Assert(!metadataJson.Contains("\"Code\"", StringComparison.Ordinal),
                "metadata project.json must not serialize C# subfile code");
            Assert(!metadataJson.Contains("transform.Rotate", StringComparison.Ordinal),
                "metadata project.json must not contain source implementation text");

            var movementBeforeCameraEdit = File.ReadAllText(movementPath);
            var cameraLook = camera.Subfiles.First(subfile => subfile.Name.Contains("Look", StringComparison.Ordinal));
            cameraLook.Code = cameraLook.Code.Replace(
                "transform.Rotate(0f, sensitivity, 0f);",
                "transform.Rotate(0f, sensitivity * 2f, 0f);",
                StringComparison.Ordinal);

            var cameraSave = service.Save(first.Project, root, storage);
            Assert(cameraSave.Success, "editing one repository-backed subfile should save successfully");
            Assert(cameraSave.WrittenCount == 1, "editing PlayerCamera should write exactly one physical C# file");
            Assert(File.ReadAllText(movementPath) == movementBeforeCameraEdit,
                "editing PlayerCamera must not rewrite PlayerMovement");
            Assert(File.ReadAllText(cameraPath).Contains("sensitivity * 2f", StringComparison.Ordinal),
                "PlayerCamera physical source should contain the Code Loom subfile edit");

            var reloaded = service.Load(root, storage.LoadRepositoryMetadata(root));
            var reloadedFiles = reloaded.Project.Folders.SelectMany(folder => folder.Files).ToList();
            Assert(reloadedFiles.Count == 2, "closing/reopening projection should restore both physical files");
            Assert(reloadedFiles.Single(file => file.Name == "PlayerCamera.cs").Subfiles.Any(subfile =>
                    subfile.Code.Contains("sensitivity * 2f", StringComparison.Ordinal)),
                "reopened PlayerCamera should restore its edited physical source into subfiles");

            // If the same file changes both outside and inside Code Loom, the physical
            // external edit wins by preservation: Save must stop instead of overwriting.
            var reloadedCamera = reloadedFiles.Single(file => file.Name == "PlayerCamera.cs");
            var externalText = File.ReadAllText(cameraPath) + "\n// external edit\n";
            File.WriteAllText(cameraPath, externalText);
            var reloadedLook = reloadedCamera.Subfiles.First(subfile => subfile.Name.Contains("Look", StringComparison.Ordinal));
            reloadedLook.Code += "\n// Code Loom edit";

            var conflictSave = service.Save(reloaded.Project, root, storage);
            Assert(!conflictSave.Success && conflictSave.Conflicts.Count == 1,
                "overlapping external and Code Loom edits should produce one source conflict");
            Assert(File.ReadAllText(cameraPath) == externalText,
                "source conflict must preserve the external physical C# edit exactly");

            // When the repository itself is the Unity project, scripts already under
            // Assets are live Unity source and must not be duplicated in Generated.
            var unityResult = new RepositoryAwareUnityExportService(new UnityExportService())
                .Export(reloaded.Project, root, root);
            Assert(unityResult.Success, "repository-aware Unity export should succeed");
            var generatedRoot = Path.Combine(root, "Assets", "CodeLoom", "Generated");
            Assert(!Directory.Exists(generatedRoot)
                   || !Directory.EnumerateFiles(generatedRoot, "*.cs", SearchOption.AllDirectories).Any(),
                "repository-backed Assets scripts must not be duplicated under CodeLoom/Generated");

            // Rename detection is conservative: a unique same-content move is reported
            // as a rename instead of an unrelated remove+add pair.
            var scanner = new RepositoryCSharpScanner();
            var baseline = service.CreateProjectSnapshot(reloaded.Project);
            var renamedPath = Path.Combine(root, "Assets", "Scripts", "Player", "PlayerMover.cs");
            File.Move(movementPath, renamedPath);
            var renameScan = scanner.Scan(root, baseline);
            Assert(renameScan.Changes.Any(change =>
                    change.Kind == RepositoryCSharpChangeKind.Renamed
                    && change.PreviousRelativePath == "Assets/Scripts/Player/PlayerMovement.cs"
                    && change.RelativePath == "Assets/Scripts/Player/PlayerMover.cs"),
                "physical C# rename should be detected when content identity is unambiguous");
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
