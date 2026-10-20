using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Infrastructure;

namespace Dashboard.Tests;

public sealed class UploadCatalogTests
{
    [Fact]
    public void ExpiredUploadFilesAreRemovedAfterRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "finops-expiry-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = new UploadCatalog(root);
            var id = Guid.NewGuid().ToString("N")[..12];
            var path = catalog.PathFor(id, "test.csv");
            File.WriteAllText(path, "cost\n1");
            catalog.Add(new(id, 101, "test.csv", "csv", path, 6, DateTime.UtcNow.AddDays(-1), "cost", ExpiresUtc: DateTime.UtcNow.AddMinutes(-1)));
            var reloaded = new UploadCatalog(root);
            Assert.Empty(reloaded.List(101, null));
            Assert.False(File.Exists(path));
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void ExpiredUploadRemovesTheFilesFileInspectKeptBesideIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "finops-sidecar-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = new UploadCatalog(root);
            var id = Guid.NewGuid().ToString("N")[..12];
            var path = catalog.PathFor(id, "spec.pdf");
            File.WriteAllText(path, "%PDF");
            foreach (var suffix in UploadCatalog.SidecarSuffixes) File.WriteAllText(path + suffix, "extracted text");
            catalog.Add(new(id, 101, "spec.pdf", "pdf", path, 4, DateTime.UtcNow.AddDays(-1), "pdf", ExpiresUtc: DateTime.UtcNow.AddMinutes(-1)));
            catalog.Cleanup();
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    // The helper once ran as `python -c <script>`; on Windows it outgrew the 32,767-character command-line limit and every
    // upload failed to start ("The filename or extension is too long"), which no Linux test run could show.
    [Fact]
    public async Task TheHelperStartsThroughTheHostWhateverItsSize()
    {
        var path = Path.Combine(TempFileHelper.UploadRoot, "0000000000aa_helper-launch-test.csv");
        try
        {
            await File.WriteAllTextAsync(path, "month,cost\nJan,10\nFeb,20\n");
            var request = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?> { ["mode"] = "count", ["kind"] = "csv", ["path"] = path });

            using var response = System.Text.Json.JsonDocument.Parse(await UploadedFileTools.RunPythonAsync(request));

            Assert.True(response.RootElement.GetProperty("ok").GetBoolean(), response.RootElement.GetRawText());
            Assert.Equal(2, response.RootElement.GetProperty("total_rows").GetInt32());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SidecarSuffixesMatchTheOnesFileInspectWrites()
    {
        using var stream = typeof(UploadedFileTools).Assembly.GetManifestResourceStream("AzureFinOps.Dashboard.AI.Tools.Resources.file_inspect.py")!;
        var script = new StreamReader(stream).ReadToEnd();
        Assert.Contains($"PDF_TEXT_SUFFIX = \"{UploadCatalog.SidecarSuffixes[0]}\"", script);
        Assert.Contains($"PDF_LOCK_SUFFIX = \"{UploadCatalog.SidecarSuffixes[1]}\"", script);
    }

    [Fact]
    public void AttachmentsPersistAndCannotCrossConversationBoundaries()
    {
        var root = Path.Combine(Path.GetTempPath(), "finops-upload-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = new UploadCatalog(root);
            var id = Guid.NewGuid().ToString("N")[..12];
            var path = catalog.PathFor(id, "test.csv");
            File.WriteAllText(path, "cost\n42.73");
            catalog.Add(new(id, 101, "test.csv", "csv", path, 10, DateTime.UtcNow, "cost", ExpiresUtc: DateTime.UtcNow.AddMinutes(30)));
            Assert.Single(catalog.BindAndList(101, "conversation-a", [id]));
            Assert.Empty(catalog.List(101, "conversation-b"));
            Assert.Throws<InvalidOperationException>(() => catalog.BindAndList(101, "conversation-b", [id]));
            Assert.Throws<InvalidOperationException>(() => catalog.Acquire(202, "conversation-a", id, out _));
            var reloaded = new UploadCatalog(root);
            using (reloaded.Acquire(101, "conversation-a", id, out var entry))
            {
                Assert.Equal(path, entry.Path);
                Assert.True(entry.ExpiresUtc > DateTime.UtcNow.AddHours(23));
                Assert.True(reloaded.Remove(101, id));
                Assert.True(File.Exists(path));
            }
            Assert.Empty(reloaded.List(101, "conversation-a"));
            Assert.Throws<InvalidOperationException>(() => reloaded.Acquire(101, "conversation-a", id, out _));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}