using System.Text.Json;
using System.Text.RegularExpressions;
using AzureFinOps.Dashboard.AI.Tools;

namespace AzureFinOps.Dashboard.Infrastructure;

internal sealed class UploadCatalog
{
    private readonly string _root;
    private readonly object _sync = new();
    private readonly Dictionary<string, UploadedFileTools.UploadEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _leases = new(StringComparer.Ordinal);

    internal UploadCatalog(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
        foreach (var path in Directory.EnumerateFiles(_root, "*.upload.json"))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<UploadedFileTools.UploadEntry>(File.ReadAllText(path));
                if (entry is null || !IsId(entry.FileId)) continue;
                var stored = PathFor(entry.FileId, entry.FileName);
                if (File.Exists(stored) && new FileInfo(stored).LinkTarget is null)
                    _entries[entry.FileId] = entry with { Path = stored };
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException) { }
        }
        Cleanup();
    }

    internal string PathFor(string id, string name)
    {
        if (!IsId(id)) throw new InvalidOperationException("Invalid upload identifier.");
        return Path.Combine(_root, id + "_" + TempFileHelper.SanitizeFilename(name, "upload"));
    }

    internal void Add(UploadedFileTools.UploadEntry entry)
    {
        if (entry.Path != PathFor(entry.FileId, entry.FileName) || !File.Exists(entry.Path) || new FileInfo(entry.Path).LinkTarget is not null)
            throw new InvalidOperationException("Invalid upload registration.");
        lock (_sync) { Persist(entry); _entries.Add(entry.FileId, entry); }
    }

    internal IReadOnlyList<UploadedFileTools.UploadEntry> BindAndList(long owner, string sessionId, IReadOnlyList<string> fileIds)
    {
        lock (_sync)
        {
            if (fileIds.Count > 10 || fileIds.Any(id => !IsId(id) || !_entries.TryGetValue(id, out var entry)
                || entry.UserId != owner || entry.Removed || entry.ExpiresUtc <= DateTime.UtcNow
                || !File.Exists(entry.Path) || new FileInfo(entry.Path).LinkTarget is not null
                || entry.SessionId is not null && entry.SessionId != sessionId))
                throw new InvalidOperationException("An attachment is unavailable in this conversation. Re-upload it; no other data source was substituted.");
            foreach (var id in fileIds.Distinct(StringComparer.Ordinal))
            {
                var entry = Touch(_entries[id] with { SessionId = sessionId });
                Persist(entry);
                _entries[id] = entry;
            }
            return List(owner, sessionId);
        }
    }

    internal IReadOnlyList<UploadedFileTools.UploadEntry> List(long owner, string? sessionId)
    {
        lock (_sync) return _entries.Values.Where(entry => entry.UserId == owner && entry.SessionId == sessionId
            && !entry.Removed && entry.ExpiresUtc > DateTime.UtcNow).OrderBy(entry => entry.CreatedUtc).ToArray();
    }

    internal IDisposable Acquire(long owner, string? sessionId, string id, out UploadedFileTools.UploadEntry entry)
    {
        lock (_sync)
        {
            if (sessionId is null || !_entries.TryGetValue(id, out var found) || found.UserId != owner || found.SessionId != sessionId
                || found.Removed || found.ExpiresUtc <= DateTime.UtcNow || !File.Exists(found.Path) || new FileInfo(found.Path).LinkTarget is not null)
                throw new InvalidOperationException("The selected file is unavailable in this conversation. Re-upload it before continuing.");
            entry = Touch(found);
            Persist(entry);
            _entries[id] = entry;
            _leases[id] = _leases.GetValueOrDefault(id) + 1;
            return new Lease(this, id);
        }
    }

    internal bool Remove(long owner, string id)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.UserId != owner || entry.Removed) return false;
            entry = entry with { Removed = true, ExpiresUtc = DateTime.UtcNow.AddMinutes(30) };
            Persist(entry);
            _entries[id] = entry;
            return true;
        }
    }

    internal void Cleanup()
    {
        lock (_sync)
        {
            foreach (var entry in _entries.Values.Where(entry => entry.ExpiresUtc <= DateTime.UtcNow && _leases.GetValueOrDefault(entry.FileId) == 0).ToArray())
            {
                try
                {
                    File.Delete(entry.Path);
                    File.Delete(MetadataPath(entry.FileId));
                    _entries.Remove(entry.FileId);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static UploadedFileTools.UploadEntry Touch(UploadedFileTools.UploadEntry entry)
    {
        var expires = DateTime.UtcNow.AddHours(24);
        var maximum = entry.CreatedUtc.AddDays(7);
        return entry with { ExpiresUtc = expires < maximum ? expires : maximum };
    }

    private void Persist(UploadedFileTools.UploadEntry entry)
    {
        var destination = MetadataPath(entry.FileId);
        var temporary = destination + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(entry));
        File.Move(temporary, destination, true);
    }

    private string MetadataPath(string id) => Path.Combine(_root, id + ".upload.json");
    private static bool IsId(string? id) => id is not null && Regex.IsMatch(id, "^[a-f0-9]{12}$", RegexOptions.CultureInvariant);

    private sealed class Lease(UploadCatalog catalog, string id) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (catalog._sync)
                if (--catalog._leases[id] == 0) catalog._leases.Remove(id);
        }
    }
}