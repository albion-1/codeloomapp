using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace codeloomapp.Services;

public sealed class ProjectHistoryService
{
    private const int MaxEntries = 40;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _historyDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodeLoom",
        "History");

    private ProjectHistoryArchive _archive = new();

    public string ContextKey { get; private set; } = string.Empty;
    public int CurrentIndex => _archive.CurrentIndex;
    public IReadOnlyList<ProjectHistoryEntry> Entries => _archive.Entries;
    public bool CanUndo => _archive.CurrentIndex > 0;
    public bool CanRedo => _archive.CurrentIndex >= 0
                           && _archive.CurrentIndex < _archive.Entries.Count - 1;

    public void Initialize(string contextKey, string currentProjectJson)
    {
        ContextKey = contextKey;
        _archive = LoadArchive(contextKey) ?? new ProjectHistoryArchive();

        if (_archive.Entries.Count == 0)
        {
            _archive.Entries.Add(ProjectHistoryEntry.Create("Opened project", currentProjectJson));
            _archive.CurrentIndex = 0;
            SaveArchive();
            return;
        }

        var matchingIndex = -1;
        for (var index = _archive.Entries.Count - 1; index >= 0; index--)
        {
            if (string.Equals(
                    _archive.Entries[index].ProjectJson,
                    currentProjectJson,
                    StringComparison.Ordinal))
            {
                matchingIndex = index;
                break;
            }
        }

        if (matchingIndex >= 0)
        {
            _archive.CurrentIndex = matchingIndex;
            SaveArchive();
            return;
        }

        if (_archive.CurrentIndex < _archive.Entries.Count - 1)
        {
            _archive.Entries.RemoveRange(
                _archive.CurrentIndex + 1,
                _archive.Entries.Count - _archive.CurrentIndex - 1);
        }

        _archive.Entries.Add(ProjectHistoryEntry.Create("Opened current project", currentProjectJson));
        TrimToLimit();
        _archive.CurrentIndex = _archive.Entries.Count - 1;
        SaveArchive();
    }

    public bool Capture(string contextKey, string projectJson, string label)
    {
        EnsureContext(contextKey, projectJson);

        if (_archive.CurrentIndex >= 0
            && _archive.CurrentIndex < _archive.Entries.Count
            && string.Equals(
                _archive.Entries[_archive.CurrentIndex].ProjectJson,
                projectJson,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (_archive.CurrentIndex < _archive.Entries.Count - 1)
        {
            _archive.Entries.RemoveRange(
                _archive.CurrentIndex + 1,
                _archive.Entries.Count - _archive.CurrentIndex - 1);
        }

        _archive.Entries.Add(ProjectHistoryEntry.Create(
            string.IsNullOrWhiteSpace(label) ? "Project change" : label.Trim(),
            projectJson));

        TrimToLimit();
        _archive.CurrentIndex = _archive.Entries.Count - 1;
        SaveArchive();
        return true;
    }

    public ProjectHistoryEntry? Undo(string contextKey, string currentProjectJson)
    {
        EnsureContext(contextKey, currentProjectJson);
        if (!CanUndo)
            return null;

        _archive.CurrentIndex--;
        SaveArchive();
        return _archive.Entries[_archive.CurrentIndex];
    }

    public ProjectHistoryEntry? Redo(string contextKey, string currentProjectJson)
    {
        EnsureContext(contextKey, currentProjectJson);
        if (!CanRedo)
            return null;

        _archive.CurrentIndex++;
        SaveArchive();
        return _archive.Entries[_archive.CurrentIndex];
    }

    public ProjectHistoryEntry? Restore(
        string contextKey,
        string currentProjectJson,
        string entryId)
    {
        EnsureContext(contextKey, currentProjectJson);

        var index = _archive.Entries.FindIndex(entry =>
            string.Equals(entry.Id, entryId, StringComparison.Ordinal));
        if (index < 0)
            return null;

        _archive.CurrentIndex = index;
        SaveArchive();
        return _archive.Entries[index];
    }

    private void EnsureContext(string contextKey, string currentProjectJson)
    {
        if (!string.Equals(ContextKey, contextKey, StringComparison.Ordinal))
            Initialize(contextKey, currentProjectJson);
    }

    private void TrimToLimit()
    {
        if (_archive.Entries.Count <= MaxEntries)
            return;

        var removeCount = _archive.Entries.Count - MaxEntries;
        _archive.Entries.RemoveRange(0, removeCount);
        _archive.CurrentIndex = Math.Max(0, _archive.CurrentIndex - removeCount);
    }

    private ProjectHistoryArchive? LoadArchive(string contextKey)
    {
        try
        {
            var path = GetArchivePath(contextKey);
            if (!File.Exists(path))
                return null;

            var json = File.ReadAllText(path);
            var archive = JsonSerializer.Deserialize<ProjectHistoryArchive>(json, JsonOptions);
            if (archive is null)
                return null;

            archive.CurrentIndex = archive.Entries.Count == 0
                ? -1
                : Math.Clamp(archive.CurrentIndex, 0, archive.Entries.Count - 1);
            return archive;
        }
        catch
        {
            return null;
        }
    }

    private void SaveArchive()
    {
        try
        {
            Directory.CreateDirectory(_historyDirectory);
            var path = GetArchivePath(ContextKey);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_archive, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // History is a convenience layer. A history-write failure must never
            // interfere with editing or autosave.
        }
    }

    private string GetArchivePath(string contextKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(contextKey));
        var shortHash = Convert.ToHexString(bytes)[..20];
        return Path.Combine(_historyDirectory, shortHash + ".json");
    }
}

public sealed class ProjectHistoryArchive
{
    public int CurrentIndex { get; set; } = -1;
    public List<ProjectHistoryEntry> Entries { get; set; } = new();
}

public sealed class ProjectHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "Project change";
    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;
    public string ProjectJson { get; set; } = string.Empty;

    public string TimeLabel => SavedAtUtc.ToLocalTime().ToString("g");

    public static ProjectHistoryEntry Create(string label, string projectJson)
    {
        return new ProjectHistoryEntry
        {
            Label = label,
            ProjectJson = projectJson,
            SavedAtUtc = DateTime.UtcNow
        };
    }
}
