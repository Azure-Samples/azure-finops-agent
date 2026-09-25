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