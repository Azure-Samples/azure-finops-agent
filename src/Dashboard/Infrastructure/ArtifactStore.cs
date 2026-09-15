using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AzureFinOps.Dashboard.Infrastructure;

internal sealed class ArtifactStore
{
    internal sealed record Entry(string Id, long? Owner, string FileName, string ContentType, DateTime ExpiresUtc,
        [property: JsonIgnore] string Path);
    private readonly string _root;
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.Ordinal)
    {
        "text/html", "text/csv", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/x-powershell", "application/x-shellscript"
    };
    internal static ArtifactStore Default { get; } = new(Path.Combine(
        Environment.GetEnvironmentVariable("COPILOT_HOME") ?? Path.Combine(Path.GetTempPath(), "copilot"), "artifacts"));

    internal ArtifactStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
        foreach (var file in Directory.EnumerateFiles(_root, "*.json"))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(file));
                if (entry is null || !Regex.IsMatch(entry.Id, "^[a-f0-9]{32}$") || !AllowedTypes.Contains(entry.ContentType)) continue;
                var path = Path.Combine(_root, entry.Id + ".data");
                if (entry.Owner is not null && entry.ExpiresUtc > DateTime.UtcNow && File.Exists(path) && new FileInfo(path).LinkTarget is null)
                    _entries[entry.Id] = entry with { Path = path };
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { }
        }
        Cleanup();
    }

    internal Entry Register(long owner, string fileName, string contentType, byte[] content)
    {
        if (!AllowedTypes.Contains(contentType) || content.Length == 0 || content.Length > 20 * 1024 * 1024)
            throw new InvalidOperationException("Unsupported artifact type or size.");
        var id = Guid.NewGuid().ToString("N");
        var entry = new Entry(id, owner, TempFileHelper.SanitizeFilename(fileName, "report"), contentType,
            DateTime.UtcNow.AddHours(24), Path.Combine(_root, id + ".data"));
        lock (_sync)
        {
            Cleanup();
            File.WriteAllBytes(entry.Path, content);
            var metadata = Path.Combine(_root, id + ".json");
            File.WriteAllText(metadata + ".tmp", JsonSerializer.Serialize(entry));
            File.Move(metadata + ".tmp", metadata, true);
            _entries[id] = entry;
        }
        return entry;
    }

    internal Entry? Find(string id, long owner)
    {
        lock (_sync) return _entries.TryGetValue(id, out var entry) && entry.Owner == owner && entry.ExpiresUtc > DateTime.UtcNow
            && File.Exists(entry.Path) && new FileInfo(entry.Path).LinkTarget is null ? entry : null;
    }

    internal bool Remove(string id, long owner)
    {
        lock (_sync)
        {
            if (Find(id, owner) is not { } entry) return false;
            File.Delete(entry.Path);
            File.Delete(Path.Combine(_root, id + ".json"));
            return _entries.Remove(id);
        }
    }

    internal void Cleanup()
    {
        lock (_sync)
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*.json"))
            {
                try
                {
                    var id = Path.GetFileNameWithoutExtension(file);
                    if (!Regex.IsMatch(id, "^[a-f0-9]{32}$")) continue;
                    var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(file));
                    if (entry is not null && entry.Owner is not null && entry.ExpiresUtc > DateTime.UtcNow) continue;
                    File.Delete(Path.Combine(_root, id + ".data"));
                    File.Delete(file);
                    _entries.Remove(id);
                }
                catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { }
            }
        }
    }
}