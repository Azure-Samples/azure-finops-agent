using System.Collections.Concurrent;

namespace AzureFinOps.Dashboard.Infrastructure;

internal static class TempFileHelper
{
    /// <summary>
    /// Dedicated directory for user uploads. It is the only tree the embedded
    /// file_inspect.py helper is permitted to read, so uploads must never be
    /// written straight into the shared temp root.
    /// </summary>
    internal static string UploadRoot { get; } = CreateUploadRoot();

    private static string CreateUploadRoot()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "finops-uploads"));
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(root);
        else
            Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return root;
    }

    internal static void CleanupOldFiles<T>(ConcurrentDictionary<string, T> files, Func<T, DateTime> getCreated, Func<T, string> getPath)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-30);
        foreach (var kvp in files)
        {
            if (getCreated(kvp.Value) < cutoff)
            {
                files.TryRemove(kvp.Key, out _);
                try { File.Delete(getPath(kvp.Value)); } catch { }
            }
        }
    }

    internal static string SanitizeFilename(string name, string fallback)
    {
        // On Linux Path.GetInvalidFileNameChars() is only '\0' and '/', so '\',
        // ':' and '..' would survive into a path the caller then composes.
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':']).ToHashSet();
        var sanitized = new string(name.Where(c => !invalid.Contains(c) && !char.IsControl(c)).ToArray()).Trim();
        return sanitized.Trim('.').Length == 0 ? fallback : sanitized;
    }
}
