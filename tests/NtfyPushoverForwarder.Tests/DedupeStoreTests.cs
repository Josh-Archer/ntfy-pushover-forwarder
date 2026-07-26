using NtfyPushoverForwarder;

namespace NtfyPushoverForwarder.Tests;

public class DedupeStoreTests
{
    [Fact]
    public void InMemory_SuppressesDuplicatesWithinWindow()
    {
        var store = new InMemoryDedupeStore();
        var now = DateTimeOffset.UtcNow;
        Assert.False(store.TryRecord("a", now, TimeSpan.FromMinutes(5), 100));
        Assert.True(store.TryRecord("a", now.AddSeconds(1), TimeSpan.FromMinutes(5), 100));
        Assert.False(store.TryRecord("b", now.AddSeconds(1), TimeSpan.FromMinutes(5), 100));
    }

    [Fact]
    public void FileStore_PersistsAcrossInstances()
    {
        var path = Path.Combine(Path.GetTempPath(), "dedupe-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var window = TimeSpan.FromMinutes(10);
            var s1 = new FileDedupeStore(path, window, 100);
            Assert.False(s1.TryRecord("fp1", DateTimeOffset.UtcNow, window, 100));

            var s2 = new FileDedupeStore(path, window, 100);
            Assert.True(s2.TryRecord("fp1", DateTimeOffset.UtcNow, window, 100));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
