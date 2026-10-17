namespace Unread.Core;

/// <summary>Widths in effective pixels; hidden panes and their dividers occupy no space.</summary>
public readonly record struct PaneLayout(double Sidebar, double ArticleList, double Reader)
{
    public const double DividerWidth = 8;
    public const double DefaultSidebarWidth = 248;
    public const double DefaultArticleListWidth = 390;
    public const double MinimumSidebarWidth = 220;
    public const double MinimumArticleListWidth = 280;
    public const double MinimumReaderWidth = 360;

    public static PaneLayout Calculate(double width, double sidebarWidth, double listWidth,
        bool showSidebar, bool showList, bool showReader)
    {
        if (!double.IsFinite(width) || width <= 0) return default;
        if (!showList) return new(0, 0, showReader ? width : 0);

        var dividers = (showSidebar ? DividerWidth : 0) + (showReader ? DividerWidth : 0);
        var available = Math.Max(0, width - dividers);
        // The native window enforces a minimum size. Scaling also keeps initial/transient layouts safe.
        var scale = Math.Min(1, available / ((showSidebar ? MinimumSidebarWidth : 0)
            + MinimumArticleListWidth + (showReader ? MinimumReaderWidth : 0)));
        var listMinimum = MinimumArticleListWidth * scale;
        var readerMinimum = showReader ? MinimumReaderWidth * scale : 0;
        var sidebar = showSidebar ? Math.Clamp(Preferred(sidebarWidth, DefaultSidebarWidth),
            MinimumSidebarWidth * scale, Math.Max(MinimumSidebarWidth * scale, available - listMinimum - readerMinimum)) : 0;
        var list = showReader ? Math.Clamp(Preferred(listWidth, DefaultArticleListWidth),
            listMinimum, Math.Max(listMinimum, available - sidebar - readerMinimum)) : available - sidebar;
        return new(sidebar, list, showReader ? Math.Max(0, available - sidebar - list) : 0);
    }

    public double MaximumSidebarWidth => Math.Max(MinimumSidebarWidth, Sidebar + ArticleList - MinimumArticleListWidth);
    public double MaximumArticleListWidth => Math.Max(MinimumArticleListWidth, ArticleList + Reader - MinimumReaderWidth);

    // Moving a divider transfers space only between the two panes that meet at it.
    public PaneLayout ResizeSidebar(double width)
    {
        if (Sidebar <= 0 || ArticleList <= 0 || !double.IsFinite(width)) return this;
        var sidebar = Math.Clamp(width, MinimumSidebarWidth, MaximumSidebarWidth);
        return this with { Sidebar = sidebar, ArticleList = ArticleList + Sidebar - sidebar };
    }

    public PaneLayout ResizeArticleList(double width)
    {
        if (ArticleList <= 0 || Reader <= 0 || !double.IsFinite(width)) return this;
        var list = Math.Clamp(width, MinimumArticleListWidth, MaximumArticleListWidth);
        return this with { ArticleList = list, Reader = Reader + ArticleList - list };
    }

    private static double Preferred(double value, double fallback) => double.IsFinite(value) && value > 0 ? value : fallback;
}
