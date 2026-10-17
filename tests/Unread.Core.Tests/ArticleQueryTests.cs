using Unread.Core;
using Xunit;

namespace Unread.Core.Tests;

public sealed class ArticleQueryTests
{
    [Theory]
    [InlineData("all", "link-read,link-unread,other-read,other-unread,child-read,child-unread,feed-read,feed-unread", "link-unread,other-unread,child-unread,feed-unread")]
    [InlineData("saved", "link-read,link-unread,other-read,child-unread,feed-read,feed-unread", "link-unread,child-unread,feed-unread")]
    [InlineData("feed", "feed-read,feed-unread", "feed-unread")]
    [InlineData("folder", "child-read,child-unread,feed-read,feed-unread", "child-unread,feed-unread")]
    [InlineData("child-folder", "child-read,child-unread", "child-unread")]
    [InlineData(LocalLibrary.SavedLinksId, "link-read,link-unread", "link-unread")]
    public void One_filter_applies_to_every_source_without_changing_article_state(string source, string all, string unread)
    {
        var library = Library();
        var settings = new ReaderSettings();
        var originalStates = library.Articles.Select(a => (a.Id, a.IsUnread, a.IsSaved)).ToArray();
        Assert.Equal(all.Split(','), ArticleQuery.Apply(library, source, settings.UnreadOnly).Select(a => a.Id));
        settings.UnreadOnly = true;
        Assert.Equal(unread.Split(','), ArticleQuery.Apply(library, source, settings.UnreadOnly).Select(a => a.Id));
        Assert.Equal(originalStates, library.Articles.Select(a => (a.Id, a.IsUnread, a.IsSaved)));
    }

    [Fact]
    public void Search_intersects_scope_and_read_filter_without_selecting_unrelated_articles()
    {
        var library = Library();
        library.Articles[0].Title = "Matching title";
        library.Articles[1].Html = "<p>Matching body</p>";
        library.Articles[2].FeedTitle = "Matching source";
        library.Articles[4].Title = "Matching outside folder";
        Assert.Equal(new[] { "child-unread", "feed-read", "feed-unread" },
            ArticleQuery.Apply(library, "folder", false, "  MATCHING ").Select(a => a.Id));
        Assert.Equal(new[] { "child-unread", "feed-unread" },
            ArticleQuery.Apply(library, "folder", true, "matching").Select(a => a.Id));
        Assert.Empty(ArticleQuery.Apply(library, "folder", true, "absent"));
    }

    [Fact]
    public async Task The_global_filter_defaults_to_all_and_survives_restart_and_library_changes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "UnreadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new LibraryStore(directory);
            Assert.False((await store.LoadSettingsAsync()).UnreadOnly);
            await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"), "{\"Theme\":\"Dusk\",\"Library\":\"feedly\"}");
            var settings = await store.LoadSettingsAsync();
            Assert.False(settings.UnreadOnly);
            settings.UnreadOnly = true;
            await store.SaveSettingsAsync(settings);
            var restarted = await new LibraryStore(directory).LoadSettingsAsync();
            Assert.True(restarted.UnreadOnly);
            restarted.Library = "local";
            await store.SaveSettingsAsync(restarted);
            var local = await new LibraryStore(directory).LoadSettingsAsync();
            Assert.True(local.UnreadOnly);
            Assert.Equal("Dusk", local.Theme);
            local.UnreadOnly = false;
            await store.SaveSettingsAsync(local);
            Assert.False((await new LibraryStore(directory).LoadSettingsAsync()).UnreadOnly);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static LibrarySnapshot Library()
    {
        var library = new LibrarySnapshot
        {
            Folders = [new("folder", "Folder") { Path = ["Folder"] }, new("child-folder", "Child") { Path = ["Folder", "Child"] }],
            Subscriptions = [new("feed", "Feed", [new("folder", "Folder")]), new("child", "Child", [new("child-folder", "Child")]), new("other", "Other", [])],
            Articles = [Article("feed-unread", "feed", true, true, 1), Article("feed-read", "feed", false, true, 2),
                Article("child-unread", "child", true, true, 3), Article("child-read", "child", false, false, 4),
                Article("other-unread", "other", true, false, 5), Article("other-read", "other", false, true, 6),
                Article("link-unread", LocalLibrary.SavedLinksId, true, true, 7), Article("link-read", LocalLibrary.SavedLinksId, false, true, 8)]
        };
        return library;
    }

    private static Article Article(string id, string feed, bool unread, bool saved, int day) => new()
    { Id = id, FeedId = feed, IsUnread = unread, IsSaved = saved, Published = new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero) };
}
