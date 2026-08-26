namespace codeloomapp.Services;

public static class GitExecutableLocator
{
    public static string Resolve()
    {
        foreach (var bundled in BundledCandidates())
        {
            if (File.Exists(bundled))
                return bundled;
        }

        var pathGit = FindExecutableOnPath("git.exe");
        if (!string.IsNullOrWhiteSpace(pathGit))
            return pathGit;

        foreach (var candidate in InstalledCandidates())
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return "git";
    }

    public static void EnsureOnProcessPath()
    {
        var resolved = Resolve();
        if (!Path.IsPathRooted(resolved) || !File.Exists(resolved))
            return;

        var directory = Path.GetDirectoryName(resolved);
        if (string.IsNullOrWhiteSpace(directory))
            return;

        var current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var entries = current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        if (entries.Any(entry =>
                string.Equals(entry.Trim().Trim('"'), directory, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        Environment.SetEnvironmentVariable(
            "PATH",
            directory + Path.PathSeparator + current,
            EnvironmentVariableTarget.Process);
    }

    public static string DescribeResolvedPath()
    {
        var resolved = Resolve();
        try
        {
            return Path.IsPathRooted(resolved) ? Path.GetFullPath(resolved) : resolved;
        }
        catch
        {
            return resolved;
        }
    }

    private static IEnumerable<string> BundledCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "tools", "git", "cmd", "git.exe");
        yield return Path.Combine(AppContext.BaseDirectory, "tools", "git", "bin", "git.exe");
        yield return Path.Combine(AppContext.BaseDirectory, "tools", "git", "mingw64", "bin", "git.exe");
    }

    private static IEnumerable<string> InstalledCandidates()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        yield return Path.Combine(programFiles, "Git", "cmd", "git.exe");
        yield return Path.Combine(programFiles, "Git", "bin", "git.exe");
        yield return Path.Combine(programFilesX86, "Git", "cmd", "git.exe");
        yield return Path.Combine(localAppData, "Programs", "Git", "cmd", "git.exe");

        foreach (var version in new[] { "2026", "18", "2022", "2019" })
        {
            foreach (var edition in new[] { "Community", "Professional", "Enterprise", "BuildTools" })
            {
                yield return Path.Combine(
                    programFiles,
                    "Microsoft Visual Studio",
                    version,
                    edition,
                    "Common7",
                    "IDE",
                    "CommonExtensions",
                    "Microsoft",
                    "TeamFoundation",
                    "Team Explorer",
                    "Git",
                    "cmd",
                    "git.exe");
            }
        }
    }

    private static string? FindExecutableOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        foreach (var part in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var folder = part.Trim().Trim('"');
                var candidate = Path.Combine(folder, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
            }
        }

        return null;
    }
}
