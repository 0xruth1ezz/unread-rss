using Unread.Core;
using Xunit;

namespace Unread.Core.Tests;

public sealed class PaneLayoutTests
{
    [Fact]
    public void Sidebar_divider_transfers_space_from_the_list_without_moving_the_reader()
    {
        var original = Desktop(1480, 248, 390);
        var resized = original.ResizeSidebar(348);
        Assert.Equal(348, resized.Sidebar);
        Assert.Equal(290, resized.ArticleList);
        Assert.Equal(original.Reader, resized.Reader);
        Assert.Equal(PaneLayout.MinimumArticleListWidth, original.ResizeSidebar(10000).ArticleList);
        Assert.Equal(PaneLayout.MinimumSidebarWidth, original.ResizeSidebar(-10000).Sidebar);
    }

    [Fact]
    public void List_divider_can_resize_the_reader_but_cannot_hide_either_pane()
    {
        var original = Desktop(1480, 248, 390);
        var resized = original.ResizeArticleList(500);
        Assert.Equal(original.Sidebar, resized.Sidebar);
        Assert.Equal(500, resized.ArticleList);
        Assert.Equal(original.Reader - 110, resized.Reader);
        Assert.Equal(PaneLayout.MinimumReaderWidth, original.ResizeArticleList(10000).Reader);
        Assert.Equal(PaneLayout.MinimumArticleListWidth, original.ResizeArticleList(-10000).ArticleList);
    }

    [Fact]
    public void A_small_window_clamps_preferences_without_destroying_them()
    {
        var settings = new ReaderSettings { SidebarWidth = 400, ArticleListWidth = 600 };
        var small = Desktop(1100, settings.SidebarWidth, settings.ArticleListWidth);
        Assert.Equal(400, small.Sidebar);
        Assert.Equal(324, small.ArticleList);
        Assert.Equal(360, small.Reader);
        var restored = Desktop(1480, settings.SidebarWidth, settings.ArticleListWidth);
        Assert.Equal(400, restored.Sidebar);
        Assert.Equal(600, restored.ArticleList);
        Assert.Equal(464, restored.Reader);
    }

    [Fact]
    public void Compact_browser_focus_and_hidden_sidebar_return_the_unused_space()
    {
        var compact = PaneLayout.Calculate(760, 248, 390, true, true, false);
        Assert.Equal(new PaneLayout(248, 504, 0), compact);
        Assert.Equal(new PaneLayout(472, 280, 0), compact.ResizeSidebar(10000));
        Assert.Equal(new PaneLayout(0, 760, 0), PaneLayout.Calculate(760, 248, 390, false, true, false));
        Assert.Equal(new PaneLayout(0, 0, 760), PaneLayout.Calculate(760, 248, 390, false, false, true));
        Assert.Equal(new PaneLayout(0, 390, 1082), PaneLayout.Calculate(1480, 248, 390, false, true, true));
    }

    [Theory]
    [InlineData(double.NaN, double.PositiveInfinity)]
    [InlineData(0, -1)]
    public void Invalid_saved_widths_fall_back_to_a_usable_layout(double sidebar, double list)
    {
        Assert.Equal(Desktop(1480, 248, 390), Desktop(1480, sidebar, list));
    }

    [Theory]
    [InlineData(1100, 100000, 100000)]
    [InlineData(1480, 248, 390)]
    [InlineData(3840, 600, 900)]
    public void All_visible_columns_and_dividers_fit_the_window(double width, double sidebar, double list)
    {
        var result = Desktop(width, sidebar, list);
        Assert.True(result.Sidebar >= PaneLayout.MinimumSidebarWidth);
        Assert.True(result.ArticleList >= PaneLayout.MinimumArticleListWidth);
        Assert.True(result.Reader >= PaneLayout.MinimumReaderWidth);
        Assert.Equal(width, result.Sidebar + result.ArticleList + result.Reader + 2 * PaneLayout.DividerWidth);
    }

    [Fact]
    public async Task Widths_survive_restart_and_older_settings_keep_defaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), "UnreadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"), "{\"Theme\":\"Dusk\"}");
            var old = await new LibraryStore(directory).LoadSettingsAsync();
            Assert.Equal(248, old.SidebarWidth);
            Assert.Equal(390, old.ArticleListWidth);
            old.SidebarWidth = 320; old.ArticleListWidth = 460;
            await new LibraryStore(directory).SaveSettingsAsync(old);
            var restored = await new LibraryStore(directory).LoadSettingsAsync();
            Assert.Equal(320, restored.SidebarWidth);
            Assert.Equal(460, restored.ArticleListWidth);
            Assert.Equal("Dusk", restored.Theme);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static PaneLayout Desktop(double width, double sidebar, double list) => PaneLayout.Calculate(width, sidebar, list, true, true, true);
}
