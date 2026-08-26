using codeloomapp.Services;

internal static class RepositoryScannerSmokeTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeLoomScannerSmoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            Write(root, "Assets/Scripts/Player.cs", "public class Player { }");
            Write(root, "Assets/Scripts/notes.txt", "not C#");
            Write(root, "Assets/CodeLoom/Generated/Generated.cs", "public class Generated { }");
            Write(root, "Library/PackageCache/Cached.cs", "public class Cached { }");
            Write(root, "Packages/PackageCode.cs", "public class PackageCode { }");
            Write(root, "obj/Debug/Temporary.cs", "public class Temporary { }");

            var scanner = new RepositoryCSharpScanner();
            var first = scanner.Scan(root);

            Assert(first.IsFirstScan, "first repository scan should be identified as the baseline scan");
            Assert(first.Files.Count == 1, "scanner should include only user-controlled .cs files outside excluded directories");
            Assert(first.Files[0].RelativePath == "Assets/Scripts/Player.cs", "scanner returned the wrong C# source path");
            Assert(first.Changes.Count == 1 && first.Changes[0].Kind == RepositoryCSharpChangeKind.Added,
                "first scan should report discovered C# source as new");

            var baseline = RepositoryCSharpScanner.CreateSnapshot(first);
            Write(root, "Assets/Scripts/Player.cs", "public class Player { public int Health = 10; }");
            Write(root, "Assets/Scripts/Enemy.cs", "public class Enemy { }");

            var second = scanner.Scan(root, baseline);
            Assert(second.Changes.Any(change =>
                    change.RelativePath == "Assets/Scripts/Player.cs"
                    && change.Kind == RepositoryCSharpChangeKind.Changed),
                "changed C# source should be detected");
            Assert(second.Changes.Any(change =>
                    change.RelativePath == "Assets/Scripts/Enemy.cs"
                    && change.Kind == RepositoryCSharpChangeKind.Added),
                "new C# source should be detected");

            var secondBaseline = RepositoryCSharpScanner.CreateSnapshot(second);
            File.Delete(Path.Combine(root, "Assets", "Scripts", "Player.cs"));

            var third = scanner.Scan(root, secondBaseline);
            Assert(third.Changes.Count == 1, "removed-file scan should report exactly one change");
            Assert(third.Changes[0].RelativePath == "Assets/Scripts/Player.cs"
                   && third.Changes[0].Kind == RepositoryCSharpChangeKind.Removed,
                "removed C# source should be detected");
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

    private static void Write(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
