using System.Net;
using Unread.Core;
using Xunit;

namespace Unread.Core.Tests;

public sealed class SyncTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "UnreadTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Offline_changes_survive_restart_and_replay_without_losing_intent()
    {
        var store = new LibraryStore(_directory);
        var fail = true;
        var handler = new StubHandler(_ => new(fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        var sync = new SyncEngine(new(new(handler), () => "test"), store);
        var article = new Article { Id = "1", FeedId = "feed/1", IsUnread = true };
        var library = new LibrarySnapshot { UserId = "person", Articles = [article], UnreadCounts = new() { ["feed/1"] = 4, ["user/person/category/global.all"] = 8 } };
        await sync.ChangeAsync(library, article, "read", true);
        await sync.ChangeAsync(library, article, "saved", true);
        await Assert.ThrowsAsync<FeedlyException>(() => sync.FlushAsync(library));
        var restored = (await store.LoadAsync())!;
        Assert.Equal(2, restored.Pending.Count); Assert.False(restored.Articles[0].IsUnread); Assert.True(restored.Articles[0].IsSaved);
        Assert.Equal(3, restored.UnreadCounts["feed/1"]); Assert.Equal(7, restored.UnreadCounts["user/person/category/global.all"]);
        fail = false;
        await sync.FlushAsync(restored);
        Assert.Empty((await store.LoadAsync())!.Pending);
    }

    [Fact]
    public async Task Repeated_toggles_coalesce_to_last_intent_and_counts_do_not_drift()
    {
        var store = new LibraryStore(_directory);
        var sync = new SyncEngine(new(new(new StubHandler(_ => new(HttpStatusCode.OK))), () => "test"), store);
        var article = new Article { Id = "1", FeedId = "feed/1", IsUnread = true };
        var library = new LibrarySnapshot { Articles = [article], UnreadCounts = new() { ["feed/1"] = 1 } };
        await sync.ChangeAsync(library, article, "read", true);
        await sync.ChangeAsync(library, article, "read", true);
        Assert.Equal(0, library.UnreadCounts["feed/1"]);
        await sync.ChangeAsync(library, article, "read", false);
        Assert.Equal(1, library.UnreadCounts["feed/1"]);
        Assert.False(Assert.Single(library.Pending).Value);
    }

    [Fact]
    public void Merging_remote_data_keeps_pending_local_changes_and_deduplicates()
    {
        var library = new LibrarySnapshot { Articles = [new() { Id = "1", Title = "Old title" }], Pending = [new("1", "read", true), new("1", "saved", true)] };
        SyncEngine.Merge(library, [new() { Id = "1", Title = "New title", IsUnread = true, IsSaved = false }]);
        var article = Assert.Single(library.Articles);
        Assert.Equal("New title", article.Title); Assert.False(article.IsUnread); Assert.True(article.IsSaved);
    }

    [Fact]
    public async Task A_damaged_cache_is_preserved_for_recovery()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "library.json"), "{broken");
        Assert.Null(await new LibraryStore(_directory).LoadAsync());
        Assert.Equal("{broken", await File.ReadAllTextAsync(Path.Combine(_directory, "library.json.corrupt")));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
