using Unread.Core;
using Xunit;

namespace Unread.Core.Tests;

public sealed class SidebarOrganizationTests
{
    [Fact]
    public void Older_libraries_materialize_parents_and_keep_empty_folders_after_unsubscribing()
    {
        var feed = Feed("one", ["Reading", "Technology"]);
        var library = new LibrarySnapshot { Subscriptions = [feed] };
        SidebarOrganization.Normalize(library);
        Assert.Equal(2, library.Folders.Count);
        var parent = Assert.Single(SidebarOrganization.Children(library));
        var child = Assert.Single(SidebarOrganization.Children(library, parent.Id));
        Assert.Equal("Technology", child.Title);
        Assert.Equal(feed.Categories[0].Id, child.Id);
        Assert.Contains(feed.Id, SidebarOrganization.FeedIds(library, parent.Id));
        library.Subscriptions.Clear(); SidebarOrganization.Normalize(library);
        Assert.Equal(2, library.Folders.Count);
        Assert.Empty(SidebarOrganization.Children(library, child.Id));
    }

    [Fact]
    public void Renames_keep_ids_order_and_memberships_and_new_feeds_reuse_the_renamed_folder()
    {
        var library = Library();
        var folder = SidebarOrganization.CreateFolder(library, "News");
        var child = SidebarOrganization.CreateFolder(library, "Tech", folder.Id);
        SidebarOrganization.Move(library, library.Subscriptions[0].Id, "", child.Id);
        SidebarOrganization.RenameFolder(library, folder.Id, "Reading");
        Assert.Equal(new[] { "Reading", "Tech" }, library.Folders.Single(f => f.Id == child.Id).Path);
        Assert.Equal(child.Id, library.Subscriptions[0].Categories[0].Id);
        Assert.Equal("Reading / Tech", library.Subscriptions[0].Categories[0].Label);
        LocalLibrary.AddSubscriptions(library, [Feed("new", ["reading", "tech"])]);
        Assert.Equal(2, library.Folders.Count);
        Assert.Equal(child.Id, library.Subscriptions[^1].Categories[0].Id);
        // Recreating the old path must not reuse the renamed folder's identity.
        var replacement = SidebarOrganization.CreateFolder(library, "News");
        Assert.NotEqual(folder.Id, replacement.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("News/Tech")]
    [InlineData("Line\nBreak")]
    public void Invalid_folder_names_do_not_create_partial_folders(string name)
    {
        var library = Library();
        Assert.Throws<FormatException>(() => SidebarOrganization.CreateFolder(library, name));
        Assert.Empty(library.Folders);
    }

    [Fact]
    public void Duplicate_sibling_names_are_rejected_but_the_same_name_in_another_parent_is_valid()
    {
        var library = Library();
        var first = SidebarOrganization.CreateFolder(library, "News");
        Assert.Throws<InvalidOperationException>(() => SidebarOrganization.CreateFolder(library, "news"));
        var nested = SidebarOrganization.CreateFolder(library, "News", first.Id);
        Assert.Equal(new[] { "News", "News" }, nested.Path);
    }

    [Fact]
    public void Feed_and_folder_siblings_can_be_reordered_together_and_no_op_moves_are_detected()
    {
        var library = Library();
        var folder = SidebarOrganization.CreateFolder(library, "Reading");
        var first = library.Subscriptions[0].Id; var second = library.Subscriptions[1].Id;
        Assert.True(SidebarOrganization.Move(library, folder.Id, "", "", first));
        Assert.Equal(new[] { folder.Id, first, second }, SidebarOrganization.Children(library).Select(e => e.Id));
        Assert.True(SidebarOrganization.Move(library, first, "", "", second, after: true));
        Assert.Equal(new[] { folder.Id, second, first }, SidebarOrganization.Children(library).Select(e => e.Id));
        Assert.False(SidebarOrganization.Move(library, first, "", "", second, after: true));
        Assert.False(SidebarOrganization.Move(library, first, "", "", first));
    }

    [Fact]
    public void Moving_feeds_into_empty_folders_and_back_out_keeps_articles_and_read_state()
    {
        var library = Library(); var feed = library.Subscriptions[0];
        var folder = SidebarOrganization.CreateFolder(library, "Empty");
        library.Articles = [new() { Id = "story", FeedId = feed.Id, IsUnread = false, IsSaved = true }];
        SidebarOrganization.Move(library, feed.Id, "", folder.Id);
        Assert.Equal(folder.Id, Assert.Single(library.Subscriptions[0].Categories).Id);
        Assert.Equal(feed.Id, Assert.Single(SidebarOrganization.Children(library, folder.Id)).Id);
        Assert.DoesNotContain(SidebarOrganization.Children(library), e => e.Id == feed.Id);
        SidebarOrganization.Move(library, feed.Id, folder.Id, "");
        Assert.Empty(library.Subscriptions[0].Categories);
        Assert.Empty(SidebarOrganization.Children(library, folder.Id));
        Assert.False(Assert.Single(library.Articles).IsUnread); Assert.True(library.Articles[0].IsSaved);
    }

    [Fact]
    public void Moving_one_occurrence_of_a_multiply_filed_feed_preserves_other_memberships_and_deduplicates()
    {
        var library = Library(); var id = library.Subscriptions[0].Id;
        var a = SidebarOrganization.CreateFolder(library, "A"); var b = SidebarOrganization.CreateFolder(library, "B");
        library.Subscriptions[0] = library.Subscriptions[0] with { Categories = [a, b] };
        SidebarOrganization.Move(library, id, a.Id, b.Id);
        Assert.Equal(b.Id, Assert.Single(library.Subscriptions[0].Categories).Id);
        Assert.Single(SidebarOrganization.Children(library, b.Id));
        Assert.Empty(SidebarOrganization.Children(library, a.Id));
        Assert.Throws<InvalidOperationException>(() => SidebarOrganization.Move(library, id, a.Id, ""));
    }

    [Fact]
    public void Moving_a_folder_preserves_all_descendants_ids_and_child_order()
    {
        var library = Library();
        var news = SidebarOrganization.CreateFolder(library, "News"); var child = SidebarOrganization.CreateFolder(library, "Tech", news.Id);
        var destination = SidebarOrganization.CreateFolder(library, "Reading");
        var a = library.Subscriptions[0].Id; var b = library.Subscriptions[1].Id;
        SidebarOrganization.Move(library, a, "", child.Id); SidebarOrganization.Move(library, b, "", child.Id, a);
        SidebarOrganization.Move(library, news.Id, "", destination.Id);
        Assert.Equal(new[] { "Reading", "News", "Tech" }, library.Folders.Single(f => f.Id == child.Id).Path);
        Assert.Equal(new[] { b, a }, SidebarOrganization.Children(library, child.Id).Select(e => e.Id));
        Assert.Equal(2, SidebarOrganization.FeedIds(library, destination.Id).Count);
        Assert.All(library.Subscriptions, f => Assert.Equal(child.Id, Assert.Single(f.Categories).Id));
    }

    [Fact]
    public void Cycles_name_collisions_and_stale_targets_are_rejected_without_reparenting()
    {
        var library = Library(); var a = SidebarOrganization.CreateFolder(library, "A");
        var child = SidebarOrganization.CreateFolder(library, "Child", a.Id); var rootChild = SidebarOrganization.CreateFolder(library, "Child");
        Assert.Throws<InvalidOperationException>(() => SidebarOrganization.Move(library, a.Id, "", child.Id));
        Assert.Throws<InvalidOperationException>(() => SidebarOrganization.Move(library, a.Id, "", a.Id));
        Assert.Throws<InvalidOperationException>(() => SidebarOrganization.Move(library, child.Id, a.Id, ""));
        Assert.Throws<InvalidOperationException>(() => SidebarOrganization.Move(library, a.Id, "", "", "missing"));
        Assert.Equal(new[] { "A", "Child" }, library.Folders.Single(f => f.Id == child.Id).Path);
        Assert.Equal(3, library.Folders.Count);
        Assert.NotEqual(child.Id, rootChild.Id);
    }

    [Fact]
    public void Depth_limit_applies_to_the_whole_subtree_when_reparenting()
    {
        var library = Library();
        var deep = LocalLibrary.Folder(Enumerable.Range(1, 64).Select(i => "Level " + i));
        library.Folders.Add(deep); SidebarOrganization.Normalize(library);
        Assert.Throws<FormatException>(() => SidebarOrganization.CreateFolder(library, "Too deep", deep.Id));
        var a = SidebarOrganization.CreateFolder(library, "A"); SidebarOrganization.CreateFolder(library, "B", a.Id);
        Assert.Throws<FormatException>(() => SidebarOrganization.Move(library, a.Id, "", deep.Id));
        Assert.Equal("A", library.Folders.Single(f => f.Id == a.Id).Label);
    }

    [Fact]
    public void Removing_a_folder_promotes_feeds_and_keeps_subscriptions_articles_and_parent_order()
    {
        var library = Library(); var parent = SidebarOrganization.CreateFolder(library, "Reading");
        var folder = SidebarOrganization.CreateFolder(library, "News", parent.Id); var child = SidebarOrganization.CreateFolder(library, "Tech", folder.Id);
        var feed = library.Subscriptions[0].Id; SidebarOrganization.Move(library, feed, "", child.Id);
        library.Articles.Add(new() { Id = "one", FeedId = feed, IsSaved = true });
        SidebarOrganization.RemoveFolder(library, folder.Id);
        Assert.Single(library.Folders); Assert.Equal(2, library.Subscriptions.Count); Assert.Single(library.Articles);
        Assert.Equal(parent.Id, Assert.Single(library.Subscriptions[0].Categories).Id);
        Assert.Equal(feed, Assert.Single(SidebarOrganization.Children(library, parent.Id)).Id);
        SidebarOrganization.RemoveFolder(library, parent.Id);
        Assert.Empty(library.Folders); Assert.Empty(library.Subscriptions[0].Categories);
        Assert.Equal(2, SidebarOrganization.Children(library).Count);
    }

    [Fact]
    public void Opml_round_trip_preserves_empty_nested_folders_and_manual_sibling_order()
    {
        var library = Library(); var folder = SidebarOrganization.CreateFolder(library, "Reading & Art");
        SidebarOrganization.CreateFolder(library, "Empty", folder.Id);
        var first = library.Subscriptions[0].Id; var second = library.Subscriptions[1].Id;
        SidebarOrganization.Move(library, second, "", folder.Id);
        SidebarOrganization.Move(library, first, "", folder.Id, second, true);
        SidebarOrganization.Move(library, folder.Id, "", "");
        var restored = new LibrarySnapshot(); SidebarOrganization.Import(restored, Opml.Parse(Opml.Export(library)));
        var restoredFolder = restored.Folders.Single(f => f.Label == "Reading & Art");
        Assert.Equal(2, restored.Folders.Count);
        Assert.Equal(new[] { "Empty", "Beta", "Alpha" }, SidebarOrganization.Children(restored, restoredFolder.Id).Select(e => e.Title));
    }

    [Fact]
    public void Import_into_a_renamed_folder_keeps_existing_order_and_appends_new_items_in_opml_order()
    {
        var library = Library(); var folder = SidebarOrganization.CreateFolder(library, "Old");
        SidebarOrganization.RenameFolder(library, folder.Id, "News");
        var first = library.Subscriptions[0].Id; SidebarOrganization.Move(library, first, "", folder.Id);
        var imported = Opml.Parse("<opml><body><outline text='News'><outline text='Z' xmlUrl='https://z.example/feed'/><outline text='C' xmlUrl='https://c.example/feed'/><outline text='Empty'/></outline></body></opml>");
        Assert.Equal(2, SidebarOrganization.Import(library, imported));
        Assert.Equal(folder.Id, library.Folders.Single(f => f.Label == "News").Id);
        Assert.Equal(new[] { "Alpha", "Z", "C", "Empty" }, SidebarOrganization.Children(library, folder.Id).Select(e => e.Title));
        Assert.Equal(2, library.Folders.Count);
    }

    [Fact]
    public void Case_variants_in_opml_merge_without_losing_the_imported_order()
    {
        var library = new LibrarySnapshot();
        SidebarOrganization.Import(library, Opml.Parse("<opml><body><outline text='News'><outline text='Z' xmlUrl='https://z.example/feed'/></outline><outline text='news'><outline text='A' xmlUrl='https://a.example/feed'/></outline></body></opml>"));
        var folder = Assert.Single(library.Folders);
        Assert.Equal(new[] { "Z", "A" }, SidebarOrganization.Children(library, folder.Id).Select(e => e.Title));
    }

    [Fact]
    public void Removing_a_folder_preserves_the_custom_feed_order_when_promoting_its_contents()
    {
        var library = Library(); var folder = SidebarOrganization.CreateFolder(library, "News");
        var first = library.Subscriptions[0].Id; var second = library.Subscriptions[1].Id;
        SidebarOrganization.Move(library, first, "", folder.Id);
        SidebarOrganization.Move(library, second, "", folder.Id, first);
        SidebarOrganization.RemoveFolder(library, folder.Id);
        Assert.Equal(new[] { second, first }, SidebarOrganization.Children(library).Select(e => e.Id));
    }

    [Fact]
    public void Large_opml_imports_merge_duplicate_feeds_and_retain_folder_and_source_order()
    {
        var outlines = string.Concat(Enumerable.Range(0, 20).Select(folder =>
            $"<outline text='Folder {folder}'>" + string.Concat(Enumerable.Range(0, 50).Reverse().Select(feed =>
                $"<outline text='Feed {feed}' xmlUrl='https://feed{feed}.example/rss'/>")) + "</outline>"));
        var imported = Opml.Parse("<opml><body>" + outlines + "</body></opml>");
        Assert.Equal(50, imported.Subscriptions.Count); Assert.Equal(20, imported.Folders.Count);
        Assert.All(imported.Subscriptions, feed => Assert.Equal(20, feed.Categories.Count));
        var library = new LibrarySnapshot(); SidebarOrganization.Import(library, imported);
        var folder = imported.Folders[0];
        Assert.Equal("Feed 49", SidebarOrganization.Children(library, folder.Id)[0].Title);
        Assert.Equal("Feed 0", SidebarOrganization.Children(library, folder.Id)[^1].Title);
    }

    [Fact]
    public async Task Folder_tree_order_and_memberships_survive_restart_and_edit_copies_are_isolated()
    {
        var library = Library(); var folder = SidebarOrganization.CreateFolder(library, "Reading");
        var copy = SidebarOrganization.CopyForEdit(library);
        SidebarOrganization.Move(copy, library.Subscriptions[0].Id, "", folder.Id);
        SidebarOrganization.RenameFolder(copy, folder.Id, "News");
        Assert.Empty(library.Subscriptions[0].Categories); Assert.Equal("Reading", library.Folders[0].Label);
        Assert.DoesNotContain(folder.Id, library.SidebarOrder.Keys);
        var directory = Path.Combine(Path.GetTempPath(), "UnreadTests", Guid.NewGuid().ToString("N"));
        try
        {
            await new LibraryStore(directory).SaveAsync(copy);
            var restored = (await new LibraryStore(directory).LoadAsync())!;
            SidebarOrganization.Normalize(restored);
            Assert.Equal("News", restored.Folders[0].Label);
            Assert.Equal(folder.Id, restored.Subscriptions[0].Categories[0].Id);
            Assert.Equal(SidebarOrganization.Children(copy, folder.Id), SidebarOrganization.Children(restored, folder.Id));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static LibrarySnapshot Library() => new() { Subscriptions = [Feed("one", title: "Alpha"), Feed("two", title: "Beta")] };
    private static Subscription Feed(string name, string[]? path = null, string? title = null) =>
        LocalLibrary.Subscription($"https://{name}.example/feed", title, path is null ? [] : [LocalLibrary.Folder(path)]);
}
