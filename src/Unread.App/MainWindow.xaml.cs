using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using Unread.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Unread.App;

public sealed partial class MainWindow : Window
{
    private readonly string _dataDirectory = DataDirectory();
    private readonly LibraryStore _store;
    private readonly LibraryStore _localStore;
    private readonly LocalWebClient _web;
    private bool _local;
    private readonly CredentialStore _credentials;
    private readonly FeedlyClient _feedly;
    private readonly SyncEngine _sync;
    private readonly ObservableCollection<ArticleRow> _rows = [];
    private readonly List<NavigationItem> _navigation = [new("Unread Articles", "\uEA3A", "unread"), new("All Articles", "\uE8FD", "all"), new("Saved Articles", "\uE734", "saved")];
    private LibrarySnapshot _library = new();
    private ReaderSettings _settings = new();
    private string? _token;
    private bool _demo = true, _ready, _busy, _focus, _sidebarHidden, _compactReader;
    private string _view = "unread", _viewLabel = "Unread Articles";
    private string? _continuation;
    private CancellationTokenSource? _navigationCancellation;
    private int _viewVersion;
    private Article? _selected;
    private readonly SemaphoreSlim _accountGate = new(1, 1);
    private readonly HashSet<string> _collapsedFolders = [];
    private readonly Dictionary<string, int> _localUnreadCounts = [];
    private bool _suppressNavigation, _suppressSearch;

    public MainWindow()
    {
        InitializeComponent();
        _store = new(_dataDirectory);
        _localStore = new(Path.Combine(_dataDirectory, "Local"));
        _web = new(new HttpClient { Timeout = TimeSpan.FromSeconds(25) });
        _credentials = new(_dataDirectory);
        _feedly = new(new HttpClient { Timeout = TimeSpan.FromSeconds(25) }, () => _token);
        _sync = new(_feedly, _store);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1480, 960));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Unread.ico"));
        var presenter = AppWindow.Presenter as OverlappedPresenter;
        if (presenter is not null) presenter.PreferredMinimumWidth = 760;
        ArticleList.ItemsSource = _rows;
        MainNavigation.ItemsSource = _navigation;
        Closed += (_, _) => { _navigationCancellation?.Cancel(); _lifetime.Cancel(); };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_ready) return;
        try
        {
            SizeInitialWindow();
            _settings = await _store.LoadSettingsAsync();
            ThemeChoice.SelectedIndex = _settings.Theme switch { "Dusk" => 1, "Campfire" => 2, _ => 0 };
            FontSlider.Value = _settings.FontSize;
            AutoReadToggle.IsOn = _settings.MarkReadOnOpen;
            ApplyTheme();
            _token = Environment.GetCommandLineArgs().Contains("--demo") ? null : _credentials.Read();
            if (_token is not null && _settings.Library == "feedly")
            {
                _library = await _store.LoadAsync() ?? new();
                _demo = false;
            }
            else if (Environment.GetCommandLineArgs().Contains("--demo")) _library = DemoLibrary.Create();
            else { _library = await _localStore.LoadAsync() ?? new(); _demo = false; _local = true; }
            _ready = true;
            SelectUnreadNavigation();
            BuildFeedNavigation();
            UpdateCounts();
            ShowLocalArticles();
            UpdateAccount();
            if (!_demo) await RefreshAsync();
        }
        catch (Exception ex) { _ready = true; ShowError(ex); }
    }

    private void SizeInitialWindow()
    {
        var scale = Root.XamlRoot.RasterizationScale;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(760 * scale);
            presenter.PreferredMinimumHeight = (int)(520 * scale);
        }
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var compact = Environment.GetCommandLineArgs().Contains("--compact");
        var width = Math.Min((int)((compact ? 900 : 1480) * scale), area.Width - 48);
        var height = Math.Min((int)(960 * scale), area.Height - 48);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        ApplyLayout();
    }

    private void SelectUnreadNavigation()
    {
        _suppressNavigation = true;
        MainNavigation.SelectedIndex = 0;
        _suppressNavigation = false;
        foreach (var item in _navigation) { item.Selected = item.Key == "unread"; item.Refresh(); }
    }

    private string StreamId => _view switch
    {
        "unread" or "all" => $"user/{_library.UserId}/category/global.all",
        "saved" => $"user/{_library.UserId}/tag/global.saved",
        _ => _view
    };

    private async Task NavigateAsync(string key, string label, bool contentChanged = false)
    {
        if (_busy || (key == _view && !contentChanged)) return;
        _view = key; _viewLabel = label; _continuation = null;
        _navigationCancellation?.Cancel();
        _navigationCancellation = new();
        var version = ++_viewVersion;
        foreach (var item in _navigation) { item.Selected = item.Key == key; item.Refresh(); }
        ViewTitle.Text = label;
        BuildFeedNavigation();
        ShowLocalArticles();
        if (_demo || _local || string.IsNullOrEmpty(_library.UserId)) return;
        SetBusy(true);
        try
        {
            var page = await _feedly.GetStreamAsync(StreamId, _view == "unread", ct: _navigationCancellation.Token);
            if (version != _viewVersion) return;
            SyncEngine.Merge(_library, page.Items);
            _continuation = page.Continuation;
            await _store.SaveAsync(_library);
            ShowLocalArticles();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (version == _viewVersion) ShowError(ex); }
        finally { if (version == _viewVersion) SetBusy(false); }
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        if (_local) { await RefreshLocalAsync(); return; }
        if (_demo) { ShowNotice("You're exploring the demo library.", "Use Add to start your local library, or choose a library in the sidebar.", InfoBarSeverity.Informational); return; }
        await _accountGate.WaitAsync();
        SetBusy(true);
        try
        {
            _navigationCancellation?.Cancel();
            ++_viewVersion;
            var profile = await _feedly.GetProfileAsync();
            if (!string.IsNullOrEmpty(_library.UserId) && _library.UserId != profile.Id)
                throw new InvalidOperationException("This token belongs to a different account. Disconnect first to keep account data separate.");
            _library.UserId = profile.Id; _library.UserName = profile.Name;
            await _sync.FlushAsync(_library);
            var subscriptions = _feedly.GetSubscriptionsAsync();
            var counts = _feedly.GetUnreadCountsAsync();
            var page = _feedly.GetStreamAsync(StreamId, _view == "unread");
            await Task.WhenAll(subscriptions, counts, page);
            _library.Subscriptions = await subscriptions;
            _library.UnreadCounts = await counts;
            var result = await page;
            SyncEngine.Merge(_library, result.Items);
            _continuation = result.Continuation;
            _library.LastSync = DateTimeOffset.Now;
            await _store.SaveAsync(_library);
            Notice.IsOpen = false;
            BuildFeedNavigation(); UpdateCounts(); ShowLocalArticles(); UpdateAccount();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); _accountGate.Release(); }
    }

    private IEnumerable<Article> FilteredArticles()
    {
        IEnumerable<Article> articles = _library.Articles;
        if (_view == "unread") articles = articles.Where(a => a.IsUnread);
        else if (_view == "saved") articles = articles.Where(a => a.IsSaved);
        else if (_view != "all")
        {
            var feedIds = _library.Subscriptions.Where(s => s.Id == _view || s.Categories.Any(c => c.Id == _view)).Select(s => s.Id).ToHashSet();
            if (_local && _view == LocalLibrary.SavedLinksId) feedIds.Add(LocalLibrary.SavedLinksId);
            articles = articles.Where(a => feedIds.Contains(a.FeedId));
        }
        var query = SearchBox.Text.Trim();
        if (query.Length > 0)
            articles = articles.Where(a => a.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || a.FeedTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase) || a.Summary.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        return articles.OrderByDescending(a => a.Published);
    }

    private void ShowLocalArticles()
    {
        var selectedId = _selected?.Id;
        _rows.Clear();
        foreach (var article in FilteredArticles()) _rows.Add(new(article));
        ArticleCount.Text = $"{_rows.Count} article{(_rows.Count == 1 ? "" : "s")}{(SearchBox.Text.Length > 0 ? " found" : "")}";
        LoadMoreButton.Visibility = !_demo && !_local && _continuation is not null ? Visibility.Visible : Visibility.Collapsed;
        EmptyList.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var firstUse = _local && _library.Subscriptions.Count == 0 && _library.Articles.Count == 0;
        EmptyTitle.Text = SearchBox.Text.Length > 0 ? "No matching articles." : firstUse ? "Make room for good reading." : _view == "saved" ? "Keep something good." : "You're all caught up.";
        EmptyDescription.Text = SearchBox.Text.Length > 0 ? "Try another word in your loaded articles." : firstUse ? "Add a feed or article link, or import your subscriptions from OPML." : _view == "saved" ? "Save an article to return to it here." : _local ? "Refresh your feeds or explore All Articles." : "Sync Feedly or explore All Articles.";
        EmptyLibraryActions.Visibility = firstUse ? Visibility.Visible : Visibility.Collapsed;
        var selection = _rows.FirstOrDefault(r => r.Article.Id == selectedId);
        if (selection is null && _rows.Count > 0 && Root.ActualWidth >= 1100 && SearchBox.Text.Length == 0) selection = _rows[0];
        ArticleList.SelectedItem = selection;
        if (selection is null) { _selected = null; UpdateReader(); }
        UpdateCounts();
    }

    private void BuildFeedNavigation()
    {
        FeedNavigation.Children.Clear();
        _localUnreadCounts.Clear();
        if (_demo || _local)
        {
            foreach (var group in _library.Articles.Where(a => a.IsUnread).GroupBy(a => a.FeedId))
                _localUnreadCounts[group.Key] = group.Count();
            foreach (var subscription in _library.Subscriptions)
                foreach (var category in subscription.Categories.DistinctBy(c => c.Id))
                    _localUnreadCounts[category.Id] = _localUnreadCounts.GetValueOrDefault(category.Id) + _localUnreadCounts.GetValueOrDefault(subscription.Id);
        }
        var categories = _library.Subscriptions.SelectMany(s => s.Categories).DistinctBy(c => c.Id).OrderBy(c => c.Label).ToList();
        foreach (var category in categories)
        {
            var section = new StackPanel { Margin = new(0, 4, 0, 8) };
            var feeds = new StackPanel { Spacing = 1 };
            var heading = new Grid { ColumnSpacing = 8 };
            heading.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            heading.Children.Add(new TextBlock { Text = category.Label, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Palette.Brush("ReaderSecondary") });
            var chevron = new FontIcon { Glyph = _collapsedFolders.Contains(category.Id) ? "\uE76C" : "\uE70D", FontSize = 9, Foreground = Palette.Brush("ReaderSecondary") };
            Grid.SetColumn(chevron, 1); heading.Children.Add(chevron);
            var toggle = new Button { Content = heading, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new(0), Padding = new(12, 9, 12, 9) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, "Toggle " + category.Label + " folder");
            feeds.Visibility = _collapsedFolders.Contains(category.Id) ? Visibility.Collapsed : Visibility.Visible;
            toggle.Click += (_, _) => { if (!_collapsedFolders.Add(category.Id)) _collapsedFolders.Remove(category.Id); feeds.Visibility = _collapsedFolders.Contains(category.Id) ? Visibility.Collapsed : Visibility.Visible; chevron.Glyph = _collapsedFolders.Contains(category.Id) ? "\uE76C" : "\uE70D"; };
            var all = FeedButton("All in " + category.Label, category.Id, "\uE8B7");
            feeds.Children.Add(all);
            foreach (var feed in _library.Subscriptions.Where(s => s.Categories.Any(c => c.Id == category.Id)).OrderBy(s => s.Title))
                feeds.Children.Add(FeedButton(feed.Title, feed.Id));
            section.Children.Add(toggle); section.Children.Add(feeds);
            FeedNavigation.Children.Add(section);
        }
        foreach (var feed in _library.Subscriptions.Where(s => s.Categories.Count == 0).OrderBy(s => s.Title))
            FeedNavigation.Children.Add(FeedButton(feed.Title, feed.Id));
        if (_library.Subscriptions.Count == 0)
            FeedNavigation.Children.Add(new TextBlock { Text = _local ? "Your feeds will appear here. Add a website or import OPML to begin." : "Your Feedly subscriptions will appear here.", TextWrapping = TextWrapping.Wrap, Margin = new(12), FontSize = 13, Foreground = Palette.Brush("ReaderSecondary") });
        if (_local && _library.Articles.Any(a => a.FeedId == LocalLibrary.SavedLinksId))
            FeedNavigation.Children.Add(FeedButton("Saved Links", LocalLibrary.SavedLinksId, "\uE71B"));
    }

    private Button FeedButton(string title, string key, string glyph = "\uE8F3")
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new() { Width = new(16) }); grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, Foreground = Palette.Brush("ReaderAccent") });
        var label = new TextBlock { Text = title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Palette.Brush("ReaderForeground") }; Grid.SetColumn(label, 1); grid.Children.Add(label);
        var count = _demo || _local ? _localUnreadCounts.GetValueOrDefault(key) : _library.UnreadCounts.GetValueOrDefault(key);
        var badge = new TextBlock { Text = count > 0 ? count.ToString() : "", FontSize = 12, Foreground = Palette.Brush("ReaderAccent") }; Grid.SetColumn(badge, 2); grid.Children.Add(badge);
        var button = new Button { Content = grid, Padding = new(12, 8, 10, 8), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = _view == key ? Palette.Brush("ReaderSelection") : new SolidColorBrush(Colors.Transparent), BorderThickness = new(0), CornerRadius = new(5) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, title);
        button.Click += async (_, _) => { MainNavigation.SelectedItem = null; await NavigateAsync(key, title); };
        if (_local && _library.Subscriptions.Any(s => s.Id == key))
        {
            var menu = new MenuFlyout();
            var remove = new MenuFlyoutItem { Text = "Remove feed", Icon = new SymbolIcon(Symbol.Delete) };
            remove.Click += async (_, _) => await RemoveFeedAsync(key);
            menu.Items.Add(remove); button.ContextFlyout = menu;
        }
        return button;
    }

    private void UpdateCounts()
    {
        var count = _demo || _local ? _library.Articles.Count(a => a.IsUnread) : _library.UnreadCounts.GetValueOrDefault($"user/{_library.UserId}/category/global.all", _library.Articles.Count(a => a.IsUnread));
        _navigation[0].CountLabel = count > 0 ? count.ToString() : "";
        foreach (var item in _navigation) item.Refresh();
    }

    private void UpdateReader()
    {
        var hasArticle = _selected is not null;
        ReaderScroll.Visibility = hasArticle ? Visibility.Visible : Visibility.Collapsed;
        ReaderEmpty.Visibility = hasArticle ? Visibility.Collapsed : Visibility.Visible;
        ReaderEmptyDescription.Text = _local && _library.Articles.Count == 0 ? "Your feeds and saved links, all in one place." : "Choose an article to settle in.";
        ReadButton.IsEnabled = SaveButton.IsEnabled = hasArticle;
        OpenBrowserButton.IsEnabled = CopyLinkButton.IsEnabled = _selected is { } selected && ArticleText.TryWebUri(selected.Url, out _);
        PreviousButton.IsEnabled = hasArticle && ArticleList.SelectedIndex > 0;
        NextButton.IsEnabled = _rows.Count > 0 && ArticleList.SelectedIndex < _rows.Count - 1;
        if (_selected is not { } article) { ReadingStatus.Text = ""; return; }
        ArticleTitle.Text = article.Title;
        ArticleSource.Text = article.SourceLabel;
        var date = article.Published.ToLocalTime().ToString("MMM d, yyyy 'at' h:mm tt");
        ArticleByline.Text = string.IsNullOrWhiteSpace(article.Author) ? date : article.Author + "\n" + date;
        NativeArticleRenderer.Render(ArticleBody, article, _settings.FontSize, _demo);
        OriginalLink.Visibility = ArticleText.TryWebUri(article.Url, out _) ? Visibility.Visible : Visibility.Collapsed;
        var minutes = Math.Max(1, (int)Math.Ceiling(article.Summary.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length / 220d));
        ReadingStatus.Text = $"{minutes} min read{(_demo ? " · Demo article" : "")}";
        UpdateArticleActions();
    }

    private void UpdateArticleActions()
    {
        if (_selected is not { } article) return;
        ReadIcon.Glyph = article.IsUnread ? "\uEA3A" : "\uE73E";
        SaveIcon.Glyph = article.IsSaved ? "\uE735" : "\uE734";
        SaveIcon.Foreground = article.IsSaved ? Palette.Brush("ReaderAccent") : Palette.Brush("ReaderForeground");
        ToolTipService.SetToolTip(ReadButton, article.IsUnread ? "Mark as read (M)" : "Mark as unread (M)");
        ToolTipService.SetToolTip(SaveButton, article.IsSaved ? "Remove from saved (S)" : "Save for later (S)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ReadButton, article.IsUnread ? "Mark as read" : "Mark as unread");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SaveButton, article.IsSaved ? "Remove from saved" : "Save for later");
        _rows.FirstOrDefault(r => r.Article.Id == article.Id)?.Refresh();
        if (ArticleList.SelectedItem is ArticleRow row && ArticleList.ContainerFromItem(row) is ListViewItem container)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(container, row.AccessibleName);
    }

    private void OnArticleContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is ArticleRow row)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(args.ItemContainer, row.AccessibleName);
    }

    private async Task ChangeAsync(Article article, string kind, bool value)
    {
        await _accountGate.WaitAsync();
        try
        {
        var current = _library.Articles.FirstOrDefault(a => a.Id == article.Id);
        if (current is null) return;
        article = current;
        if (_demo || _local)
        {
            if (kind == "read") article.IsUnread = !value;
            if (kind == "saved") article.IsSaved = value;
            if (_local) await _localStore.SaveAsync(_library);
        }
        else
        {
            await _sync.ChangeAsync(_library, article, kind, value);
            try { await _sync.FlushAsync(_library); }
            catch (Exception ex) { ShowError(ex); }
        }
        UpdateCounts(); BuildFeedNavigation(); UpdateArticleActions(); UpdateAccount();
        }
        finally { _accountGate.Release(); }
    }

    private async void OnArticleSelected(object sender, SelectionChangedEventArgs e)
    {
        foreach (var row in _rows) row.Selected = ReferenceEquals(row, ArticleList.SelectedItem);
        if (ArticleList.SelectedItem is not ArticleRow selected) return;
        _selected = selected.Article;
        UpdateReader(); ReaderScroll.ChangeView(null, 0, null, true);
        if (Root.ActualWidth < 1100) { _compactReader = true; ApplyLayout(); }
        if (_settings.MarkReadOnOpen && _selected.IsUnread)
        { try { await ChangeAsync(_selected, "read", true); } catch (Exception ex) { ShowError(ex); } }
    }

    private void OnArticleActivated(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ArticleRow row) return;
        if (!ReferenceEquals(ArticleList.SelectedItem, row)) { ArticleList.SelectedItem = row; return; }
        // The selected article is already rendered. Back only hides its pane.
        if (Root.ActualWidth < 1100 && !_compactReader) { _compactReader = true; ApplyLayout(); }
    }

    private async void OnMainNavigation(object sender, SelectionChangedEventArgs e)
    { if (_ready && !_suppressNavigation && MainNavigation.SelectedItem is NavigationItem item) await NavigateAsync(item.Key, item.Title); }
    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) { if (_ready && !_suppressSearch) ShowLocalArticles(); }
    private async void OnToggleRead(object sender, RoutedEventArgs e)
    { if (_selected is { } article) { try { await ChangeAsync(article, "read", article.IsUnread); } catch (Exception ex) { ShowError(ex); } } }
    private async void OnToggleSaved(object sender, RoutedEventArgs e)
    { if (_selected is { } article) { try { await ChangeAsync(article, "saved", !article.IsSaved); } catch (Exception ex) { ShowError(ex); } } }

    private async void OnMarkAllRead(object sender, RoutedEventArgs e)
    {
        var articles = _rows.Select(r => r.Article).Where(a => a.IsUnread).ToList();
        if (articles.Count == 0) return;
        await _accountGate.WaitAsync();
        try
        {
            foreach (var article in articles)
            {
                if (!_library.Articles.Any(a => a.Id == article.Id)) continue;
                if (_demo || _local) article.IsUnread = false;
                else await _sync.ChangeAsync(_library, article, "read", true);
            }
            if (_local) await _localStore.SaveAsync(_library);
            else if (!_demo) { try { await _sync.FlushAsync(_library); } catch (Exception ex) { ShowError(ex); } }
            UpdateCounts(); BuildFeedNavigation(); ShowLocalArticles(); UpdateArticleActions(); UpdateAccount();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _accountGate.Release(); }
    }

    private async void OnLoadMore(object sender, RoutedEventArgs e)
    {
        if (_busy || _continuation is null || _demo || _local) return;
        var version = _viewVersion;
        SetBusy(true);
        try
        {
            var page = await _feedly.GetStreamAsync(StreamId, _view == "unread", _continuation);
            if (version != _viewVersion) return;
            SyncEngine.Merge(_library, page.Items); _continuation = page.Continuation;
            await _store.SaveAsync(_library); ShowLocalArticles();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { if (version == _viewVersion) SetBusy(false); }
    }

    private void MoveArticle(int direction)
    {
        if (_rows.Count == 0) return;
        var index = Math.Clamp(ArticleList.SelectedIndex + direction, 0, _rows.Count - 1);
        ArticleList.SelectedIndex = index; ArticleList.ScrollIntoView(_rows[index]);
    }
    private void OnNext(object sender, RoutedEventArgs e) => MoveArticle(1);
    private void OnPrevious(object sender, RoutedEventArgs e) => MoveArticle(-1);
    private async void OnOpenBrowser(object sender, RoutedEventArgs e)
    { if (_selected is { } article && ArticleText.TryWebUri(article.Url, out var uri)) await Launcher.LaunchUriAsync(uri); }
    private void OnCopyLink(object sender, RoutedEventArgs e)
    {
        if (_selected is { } article && ArticleText.TryWebUri(article.Url, out _))
        { var package = new DataPackage(); package.SetText(article.Url); Clipboard.SetContent(package); ShowNotice("Link copied", "The article link is on your clipboard.", InfoBarSeverity.Success); }
        else if (_demo) ShowNotice("Demo article", "Connect Feedly to open or share your own articles.", InfoBarSeverity.Informational);
    }

    private async void OnFeedlyAccount(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await _accountGate.WaitAsync();
        ContentDialogResult result;
        try
        {
        var input = new PasswordBox { Header = "Feedly access token", PlaceholderText = "Paste your token", PasswordRevealMode = PasswordRevealMode.Peek, MinWidth = 360 };
        var description = new TextBlock { Text = _demo || _local ? "Connect your Feedly account to bring your feeds, folders and saved articles here." : $"Connected as {_library.UserName}. You can replace an expired token for this account.", TextWrapping = TextWrapping.Wrap };
        var help = new HyperlinkButton { Content = "Feedly API access and token help", NavigateUri = new("https://developers.feedly.com/reference/authorization"), Padding = new(0, 4, 0, 4) };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Palette.Brush("ReaderAccent"), Visibility = Visibility.Collapsed };
        var stack = new StackPanel { Spacing = 16 };
        stack.Children.Add(description); stack.Children.Add(input); stack.Children.Add(help);
        stack.Children.Add(new TextBlock { Text = "Your token is encrypted for your Windows user. Feedly controls API access; an ordinary account may require approved app credentials.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        stack.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Feedly account", Content = stack, PrimaryButtonText = "Connect", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, RequestedTheme = Root.RequestedTheme };
        if (!_demo && !_local) dialog.SecondaryButtonText = "Disconnect";
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral(); args.Cancel = true;
            if (string.IsNullOrWhiteSpace(input.Password)) { error.Text = "Paste a Feedly access token to connect."; error.Visibility = Visibility.Visible; deferral.Complete(); return; }
            dialog.IsPrimaryButtonEnabled = false;
            var oldToken = _token;
            try
            {
                var candidate = input.Password.Trim();
                if (candidate.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) candidate = candidate[7..].Trim();
                var validationClient = new FeedlyClient(new HttpClient { Timeout = TimeSpan.FromSeconds(25) }, () => candidate);
                var profile = await validationClient.GetProfileAsync();
                if (!_demo && !_local && _library.UserId != profile.Id) throw new InvalidOperationException("Disconnect before connecting a different account.");
                _credentials.Save(candidate); _token = candidate;
                if (_demo || _local) _library = await _store.LoadAsync() is { } cached && cached.UserId == profile.Id ? cached : new() { UserId = profile.Id, UserName = profile.Name };
                _demo = false; _local = false; _settings.Library = "feedly"; _selected = null; _view = "unread"; _viewLabel = "Unread Articles";
                await _store.SaveAsync(_library);
                await _store.SaveSettingsAsync(_settings);
                input.Password = ""; args.Cancel = false;
            }
            catch (Exception ex) { _token = oldToken; error.Text = ex is HttpRequestException ? "Feedly could not be reached. Check your connection and try again." : ex.Message; error.Visibility = Visibility.Visible; }
            finally { dialog.IsPrimaryButtonEnabled = true; deferral.Complete(); }
        };
        result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Secondary)
        {
            if (_library.Pending.Count > 0)
            {
                var warning = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Sync before disconnecting?", Content = $"{_library.Pending.Count} changes have not reached Feedly. Disconnecting removes the local library and these changes.", PrimaryButtonText = "Disconnect and discard", CloseButtonText = "Keep account", RequestedTheme = Root.RequestedTheme };
                if (await warning.ShowAsync() != ContentDialogResult.Primary) return;
            }
            _navigationCancellation?.Cancel(); ++_viewVersion;
            _credentials.Clear(); _token = null; _demo = false; _local = true; _settings.Library = "local";
            await _store.SaveAsync(new());
            await _store.SaveSettingsAsync(_settings);
            _library = await _localStore.LoadAsync() ?? new(); _selected = null; _view = "unread"; _viewLabel = "Unread Articles";
            ViewTitle.Text = _viewLabel; SelectUnreadNavigation();
            Notice.IsOpen = false; BuildFeedNavigation(); ShowLocalArticles(); UpdateAccount();
        }
        }
        catch (Exception ex) { ShowError(ex); return; }
        finally { _accountGate.Release(); }
        if (result == ContentDialogResult.Primary) { ViewTitle.Text = _viewLabel; SelectUnreadNavigation(); UpdateAccount(); await RefreshAsync(); }
    }

    private void UpdateAccount()
    {
        AccountLabel.Text = _demo ? "Demo library" : _local ? "Local library" : "Feedly account";
        ViewSubtitle.Text = _demo ? "Demo library" : _local ? "On this PC" : "Feedly · " + _library.UserName;
        SyncStatus.Text = _demo ? "Demo library · sample articles" : _local ? _library.LastSync is { } updated ? "Updated at " + updated.ToLocalTime().ToString("h:mm tt") : "Feeds and links stored on this PC" : _library.Pending.Count > 0 ? $"{_library.Pending.Count} changes waiting to sync" : _library.LastSync is { } date ? "Synced at " + date.ToLocalTime().ToString("h:mm tt") : "Ready to sync";
    }

    private async void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        _settings.Theme = ThemeChoice.SelectedIndex switch { 1 => "Dusk", 2 => "Campfire", _ => "Paper" };
        ApplyTheme(); BuildFeedNavigation(); foreach (var row in _rows) row.Refresh(); foreach (var item in _navigation) item.Refresh(); UpdateReader();
        try { await _store.SaveSettingsAsync(_settings); } catch (Exception ex) { ShowError(ex); }
    }

    private void ApplyTheme()
    {
        var colors = _settings.Theme switch
        {
            "Dusk" => new[] { "#232630", "#1D2029", "#EBE6DE", "#ADB2BF", "#3D414F", "#F18B7F", "#3B4050", "#A53732" },
            "Campfire" => new[] { "#282321", "#211D1B", "#F2E6D3", "#BDAE9C", "#443932", "#F29A77", "#4B3B31", "#9D3D2F" },
            _ => new[] { "#FFFFFF", "#F1F1EF", "#30312F", "#70716D", "#E4E4E0", "#D43D32", "#DCE0DB", "#D43D32" }
        };
        var keys = new[] { "ReaderBackground", "ReaderSidebar", "ReaderForeground", "ReaderSecondary", "ReaderDivider", "ReaderAccent", "ReaderSelection", "ReaderActiveBackground" };
        for (var i = 0; i < keys.Length; i++) Palette.Brush(keys[i]).Color = Color(colors[i]);
        Root.RequestedTheme = _settings.Theme == "Paper" ? ElementTheme.Light : ElementTheme.Dark;
        var foreground = Palette.Brush("ReaderForeground").Color;
        var background = Palette.Brush("ReaderSidebar").Color;
        AppWindow.TitleBar.ButtonForegroundColor = foreground;
        AppWindow.TitleBar.ButtonBackgroundColor = background;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = background;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = Palette.Brush("ReaderSecondary").Color;
    }

    private static Windows.UI.Color Color(string hex) => Windows.UI.Color.FromArgb(255, Convert.ToByte(hex.Substring(1, 2), 16), Convert.ToByte(hex.Substring(3, 2), 16), Convert.ToByte(hex.Substring(5, 2), 16));
    private async void OnFontChanged(object sender, RangeBaseValueChangedEventArgs e)
    { if (!_ready) return; _settings.FontSize = e.NewValue; UpdateReader(); try { await _store.SaveSettingsAsync(_settings); } catch (Exception ex) { ShowError(ex); } }
    private async void OnAutoReadChanged(object sender, RoutedEventArgs e)
    { if (!_ready) return; _settings.MarkReadOnOpen = AutoReadToggle.IsOn; try { await _store.SaveSettingsAsync(_settings); } catch (Exception ex) { ShowError(ex); } }

    private void SetBusy(bool busy)
    {
        _busy = busy; RefreshButton.IsEnabled = LoadMoreButton.IsEnabled = AccountButton.IsEnabled = AddButton.IsEnabled = MarkAllReadButton.IsEnabled = !busy;
        MainNavigation.IsEnabled = !busy;
        FeedNavigation.IsHitTestVisible = !busy;
        if (busy) SyncStatus.Text = _local ? "Updating your library…" : "Syncing with Feedly…"; else UpdateAccount();
    }
    private void ShowError(Exception ex)
    {
        var message = ex is HttpRequestException or TaskCanceledException ? _local ? "The website could not be reached. Check your connection or URL and try again. Your existing articles are still available." : "Feedly could not be reached. Cached articles are still available; sync again when you're online." : ex.Message;
        ShowNotice("Could not update library", message, InfoBarSeverity.Warning); UpdateAccount();
    }
    private void ShowNotice(string title, string message, InfoBarSeverity severity)
    { Notice.Title = title; Notice.Message = message; Notice.Severity = severity; Notice.IsOpen = true; }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyLayout();
        if (_ready && _selected is null && _rows.Count > 0 && e.NewSize.Width >= 1100 && SearchBox.Text.Length == 0)
            ArticleList.SelectedItem = _rows[0];
    }
    private void OnToggleSidebar(object sender, RoutedEventArgs e) { _sidebarHidden = !_sidebarHidden; ApplyLayout(); }
    private void OnToggleFocus(object sender, RoutedEventArgs e) { _focus = !_focus; ApplyLayout(); }
    private void OnReaderBack(object sender, RoutedEventArgs e) { _compactReader = false; _focus = false; ApplyLayout(); }
    private void ApplyLayout()
    {
        if (Sidebar is null) return;
        var compact = Root.ActualWidth < 1100;
        var showReader = _focus || !compact || _compactReader;
        var sidebar = !_focus && !_sidebarHidden && (!compact || !_compactReader);
        Sidebar.Visibility = sidebar ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.Width = new(sidebar ? (compact ? 220 : 248) : 0);
        ArticleListPane.Visibility = _focus || (compact && _compactReader) ? Visibility.Collapsed : Visibility.Visible;
        ListColumn.Width = _focus || (compact && _compactReader) ? new(0) : compact ? new(1, GridUnitType.Star) : new(390);
        ReaderPane.Visibility = showReader ? Visibility.Visible : Visibility.Collapsed;
        ((Grid)ReaderPane.Parent).ColumnDefinitions[2].Width = showReader ? new(1, GridUnitType.Star) : new(0);
        BackButton.Visibility = compact && showReader ? Visibility.Visible : Visibility.Collapsed;
        ArticleContent.Margin = new Thickness(compact ? 36 : 48, 35, compact ? 36 : 48, 64);
        SearchBox.Width = Root.ActualWidth < 900 ? 180 : 228;
    }

    private bool TextInputFocused => FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox or PasswordBox;
    private async void OnRefreshShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; await RefreshAsync(); }
    private void OnSearchShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { args.Handled = true; SearchBox.Focus(FocusState.Keyboard); }
    private void OnNextShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { if (!TextInputFocused) { MoveArticle(1); args.Handled = true; } }
    private void OnPreviousShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { if (!TextInputFocused) { MoveArticle(-1); args.Handled = true; } }
    private void OnReadShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { if (!TextInputFocused) { OnToggleRead(this, new()); args.Handled = true; } }
    private void OnSaveShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { if (!TextInputFocused) { OnToggleSaved(this, new()); args.Handled = true; } }
    private void OnFocusShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { _focus = !_focus; ApplyLayout(); args.Handled = true; }
    private void OnEscapeShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { _focus = _compactReader = false; ApplyLayout(); args.Handled = true; }
}
