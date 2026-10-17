using Microsoft.UI.Xaml;
using Unread.Core;

namespace Unread.App;

public sealed partial class MainWindow
{
    private PaneLayout _paneLayout;

    private void ApplyLayout()
    {
        if (ReaderPane is null || Root.ActualWidth <= 0) return;
        var compact = Root.ActualWidth < 1100;
        var showReader = _focus || !compact || _compactReader;
        var showSidebar = !_focus && !_sidebarHidden && (!compact || !_compactReader);
        var showList = !_focus && (!compact || !_compactReader);
        _paneLayout = PaneLayout.Calculate(Root.ActualWidth, _settings.SidebarWidth, _settings.ArticleListWidth,
            showSidebar, showList, showReader);

        Sidebar.Visibility = showSidebar ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.Width = new(_paneLayout.Sidebar);
        ArticleListPane.Visibility = showList ? Visibility.Visible : Visibility.Collapsed;
        ListColumn.Width = showList && !showReader ? new(1, GridUnitType.Star) : new(_paneLayout.ArticleList);
        ReaderPane.Visibility = showReader ? Visibility.Visible : Visibility.Collapsed;
        ReaderColumn.Width = showReader ? new(1, GridUnitType.Star) : new(0);

        SidebarResizer.Visibility = showSidebar && showList ? Visibility.Visible : Visibility.Collapsed;
        SidebarDividerColumn.Width = new(showSidebar && showList ? PaneLayout.DividerWidth : 0);
        ListResizer.Visibility = showList && showReader ? Visibility.Visible : Visibility.Collapsed;
        ListDividerColumn.Width = new(showList && showReader ? PaneLayout.DividerWidth : 0);
        SidebarResizer.SetRange(PaneLayout.MinimumSidebarWidth, _paneLayout.MaximumSidebarWidth, _paneLayout.Sidebar);
        ListResizer.SetRange(PaneLayout.MinimumArticleListWidth, _paneLayout.MaximumArticleListWidth, _paneLayout.ArticleList);

        BackButton.Visibility = compact && showReader ? Visibility.Visible : Visibility.Collapsed;
        ArticleContent.Margin = new Thickness(compact ? 36 : 48, 35, compact ? 36 : 48, 64);
        SearchBox.Width = Root.ActualWidth < 900 ? 180 : 228;
    }

    private async void OnPaneResize(object? sender, PaneResizeEventArgs e)
    {
        if (!_ready) return;
        if (ReferenceEquals(sender, SidebarResizer))
        {
            var resized = _paneLayout.ResizeSidebar(e.Width);
            _settings.SidebarWidth = resized.Sidebar;
            // In the compact browser the list fills the remainder; retain its desktop preference.
            if (resized.Reader > 0) _settings.ArticleListWidth = resized.ArticleList;
        }
        else _settings.ArticleListWidth = _paneLayout.ResizeArticleList(e.Width).ArticleList;
        ApplyLayout();
        if (!e.Completed) return;
        try { await _store.SaveSettingsAsync(_settings); }
        catch (Exception ex) { ShowError(ex); }
    }
}
