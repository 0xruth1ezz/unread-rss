using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Unread.Core;
using Windows.Storage.Pickers;

namespace Unread.App;

public sealed partial class MainWindow
{
    private readonly CancellationTokenSource _lifetime = new();

    private static string DataDirectory()
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--data-dir");
        return index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnreadReader");
    }

    private async Task SwitchToLocalAsync()
    {
        if (_local) return;
        _navigationCancellation?.Cancel(); ++_viewVersion;
        _library = await _localStore.LoadAsync() ?? new();
        _demo = false; _local = true; _settings.Library = "local";
        await _store.SaveSettingsAsync(_settings);
        ResetLibraryView();
    }

    private void ResetLibraryView()
    {
        ClearArticleSelection();
        _continuation = null; _view = "all"; _viewLabel = "Articles";
        SearchBox.Text = ""; ViewTitle.Text = _viewLabel; Notice.IsOpen = false;
        SelectArticlesNavigation(); BuildFeedNavigation(); ShowLocalArticles(); UpdateAccount(); ApplyLayout();
    }

    private void OnAccount(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var menu = new MenuFlyout();
        var local = new MenuFlyoutItem { Text = "Local library", Icon = new SymbolIcon(Symbol.Library) };
        local.Click += async (_, _) => { try { await SwitchToLocalAsync(); } catch (Exception ex) { ShowError(ex); } };
        menu.Items.Add(local);
        if (_token is not null && (_local || _demo))
        {
            var feedly = new MenuFlyoutItem { Text = "Open Feedly library" };
            feedly.Click += async (_, _) =>
            {
                try
                {
                    _library = await _store.LoadAsync() ?? new(); _local = _demo = false;
                    _settings.Library = "feedly"; await _store.SaveSettingsAsync(_settings);
                    ResetLibraryView(); await RefreshAsync();
                }
                catch (Exception ex) { ShowError(ex); }
            };
            menu.Items.Add(feedly);
        }
        var account = new MenuFlyoutItem { Text = "Feedly account…" };
        account.Click += OnFeedlyAccount; menu.Items.Add(account);
        menu.Items.Add(new MenuFlyoutSeparator());
        var demo = new MenuFlyoutItem { Text = "Explore demo library" };
        demo.Click += (_, _) => { _navigationCancellation?.Cancel(); ++_viewVersion; _library = DemoLibrary.Create(); _local = false; _demo = true; ResetLibraryView(); };
        menu.Items.Add(demo); menu.ShowAt(AccountButton);
    }

    private async Task RefreshLocalAsync(string? importSummary = null)
    {
        await _accountGate.WaitAsync(); SetBusy(true);
        try
        {
            var subscriptions = _library.Subscriptions.ToArray();
            if (subscriptions.Length == 0)
            {
                ShowNotice("Your local library", "Use Add to subscribe to a feed, save an article link or import OPML.", InfoBarSeverity.Informational);
                return;
            }
            using var throttle = new SemaphoreSlim(6);
            var done = 0;
            var results = await Task.WhenAll(subscriptions.Select(async feed =>
            {
                await throttle.WaitAsync(_lifetime.Token);
                try { return (Feed: feed, Result: await _web.FetchFeedAsync(feed, _lifetime.Token), Error: (Exception?)null); }
                catch (Exception ex) when (ex is not OperationCanceledException || !_lifetime.IsCancellationRequested)
                { return (Feed: feed, Result: (ParsedFeed?)null, Error: ex); }
                finally { throttle.Release(); SyncStatus.Text = $"Refreshed {++done} of {subscriptions.Length} feeds…"; }
            }));
            foreach (var result in results.Where(r => r.Result is not null))
                LocalLibrary.MergeArticles(_library, result.Result!.Articles);
            if (results.Any(r => r.Result is not null)) _library.LastSync = DateTimeOffset.Now;
            await _localStore.SaveAsync(_library);
            BuildFeedNavigation(); ShowLocalArticles();
            var failed = results.Where(r => r.Error is not null).ToArray();
            if (failed.Length > 0)
                ShowNotice($"{failed.Length} feed{(failed.Length == 1 ? "" : "s")} could not refresh",
                    (importSummary is null ? "" : importSummary + " ") + string.Join("; ", failed.Take(3).Select(r => r.Feed.Title + ": " + LocalError(r.Error!))) + (failed.Length > 3 ? "; and more." : "") + " Existing articles were kept. Try Refresh again later.", InfoBarSeverity.Warning);
            else if (importSummary is not null) ShowNotice("OPML imported", importSummary, InfoBarSeverity.Success);
            else Notice.IsOpen = false;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); _accountGate.Release(); }
    }

    private static string LocalError(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } status } => $"the website returned HTTP {(int)status}.",
        HttpRequestException => "the website could not be reached.",
        OperationCanceledException => "the request timed out.",
        System.Xml.XmlException => "the feed contains invalid XML.",
        _ => ex.Message
    };

    private async void OnAddFeed(object sender, RoutedEventArgs e) => await AddUrlAsync(false);
    private async void OnAddArticle(object sender, RoutedEventArgs e) => await AddUrlAsync(true);
    private void OnAddShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    { args.Handled = true; OnAddFeed(this, new()); }
    private void OnImportShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    { args.Handled = true; OnImportOpml(this, new()); }

    private async Task AddUrlAsync(bool articleLink)
    {
        if (_busy) return;
        try
        {
            await SwitchToLocalAsync();
            SetBusy(true);
            var url = new TextBox { Header = articleLink ? "Article URL" : "Feed or website URL", PlaceholderText = "https://", MinWidth = 340, InputScope = new InputScope { Names = { new InputScopeName { NameValue = InputScopeNameValue.Url } } } };
            var folder = new TextBox { Header = "Folder (optional)", PlaceholderText = "For example: Reading / Technology" };
            var choice = new ComboBox { Header = "Choose a feed", HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed, DisplayMemberPath = "Title" };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Palette.Brush("ReaderAccent"), Visibility = Visibility.Collapsed };
            var progress = new ProgressRing { Width = 22, Height = 22, IsActive = false, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };
            var stack = new StackPanel { Spacing = 16, MaxWidth = 440 };
            stack.Children.Add(new TextBlock { Text = articleLink ? "Save a link and a readable copy when the website makes its text available." : "Paste an RSS or Atom feed, or a website that publishes a feed.", TextWrapping = TextWrapping.Wrap });
            stack.Children.Add(url);
            if (!articleLink) { stack.Children.Add(folder); stack.Children.Add(choice); }
            stack.Children.Add(progress); stack.Children.Add(error);
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme, Title = articleLink ? "Save article link" : "Add a feed", Content = stack, PrimaryButtonText = articleLink ? "Save article" : "Find feed", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
            string? discoveredUrl = null;
            string? addedKey = null;
            url.TextChanged += (_, _) => { discoveredUrl = null; choice.Visibility = Visibility.Collapsed; dialog.PrimaryButtonText = articleLink ? "Save article" : "Find feed"; };
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                var deferral = args.GetDeferral(); args.Cancel = true;
                dialog.IsPrimaryButtonEnabled = false; dialog.IsSecondaryButtonEnabled = false; dialog.CloseButtonText = "";
                progress.Visibility = Visibility.Visible; progress.IsActive = true; error.Visibility = Visibility.Collapsed;
                try
                {
                    var address = LocalLibrary.WebUrl(url.Text);
                    if (articleLink)
                    {
                        var article = _library.Articles.FirstOrDefault(a => a.Url == address);
                        if (article is not null) article.IsSaved = true;
                        else
                        {
                            article = await _web.FetchArticleAsync(address, _lifetime.Token);
                            if (string.IsNullOrWhiteSpace(article.FeedTitle)) article.FeedTitle = new Uri(address).Host;
                            LocalLibrary.MergeArticles(_library, [article]);
                        }
                        addedKey = article.Id;
                    }
                    else
                    {
                        ParsedFeed? parsed = null;
                        var feedUrl = address;
                        if (discoveredUrl == address && choice.SelectedItem is FeedCandidate candidate) feedUrl = candidate.Url;
                        else
                        {
                            var discovery = await _web.DiscoverAsync(address, _lifetime.Token);
                            if (discovery.Feeds.Count == 0) throw new FormatException("No RSS or Atom feed was found. Try a direct feed URL, or use Save article link for a single page.");
                            if (discovery.Feeds.Count > 1)
                            {
                                choice.ItemsSource = discovery.Feeds; choice.SelectedIndex = 0; choice.Visibility = Visibility.Visible;
                                discoveredUrl = address; dialog.PrimaryButtonText = "Add selected feed"; return;
                            }
                            feedUrl = discovery.Feeds[0].Url; parsed = discovery.Feed;
                        }
                        var path = folder.Text.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                        if (path.Length > SidebarOrganization.MaximumDepth) throw new FormatException("Folders support up to 64 levels.");
                        var subscription = LocalLibrary.Subscription(feedUrl, categories: path.Length == 0 ? [] : [LocalLibrary.Folder(path)]);
                        parsed ??= await _web.FetchFeedAsync(subscription, _lifetime.Token);
                        subscription = subscription with { Title = string.IsNullOrWhiteSpace(parsed.Title) ? subscription.Title : parsed.Title, SiteUrl = parsed.SiteUrl };
                        LocalLibrary.AddSubscriptions(_library, [subscription]);
                        var existing = _library.Subscriptions.First(s => s.Id == subscription.Id);
                        foreach (var item in parsed.Articles) { item.FeedId = existing.Id; item.FeedTitle = existing.Title; }
                        LocalLibrary.MergeArticles(_library, parsed.Articles);
                        _library.LastSync = DateTimeOffset.Now;
                        addedKey = existing.Id;
                    }
                    await _localStore.SaveAsync(_library); args.Cancel = false;
                }
                catch (Exception ex) { error.Text = LocalError(ex); error.Visibility = Visibility.Visible; }
                finally { dialog.IsPrimaryButtonEnabled = true; dialog.CloseButtonText = "Cancel"; progress.IsActive = false; progress.Visibility = Visibility.Collapsed; deferral.Complete(); }
            };
            var result = await dialog.ShowAsync();
            SetBusy(false);
            if (result != ContentDialogResult.Primary) return;
            _suppressSearch = true;
            try { SearchBox.Text = ""; }
            finally { _suppressSearch = false; }
            if (articleLink)
            {
                _selected = _library.Articles.FirstOrDefault(a => a.Id == addedKey);
                _suppressNavigation = true;
                try { MainNavigation.SelectedItem = _navigation.Single(item => item.Key == "saved"); }
                finally { _suppressNavigation = false; }
                await NavigateAsync("saved", "Saved Articles", contentChanged: true);
                ArticleList.SelectedItem = _rows.FirstOrDefault(r => r.Article.Id == addedKey);
                ShowNotice("Article saved", "A copy is stored in your local library. Open the original website if the page only shared a preview.", InfoBarSeverity.Success);
            }
            else if (_library.Subscriptions.FirstOrDefault(s => s.Id == addedKey) is { } feed)
            { MainNavigation.SelectedItem = null; await NavigateAsync(feed.Id, feed.Title, contentChanged: true); ShowNotice("Feed added", feed.Title + " is in your local library.", InfoBarSeverity.Success); }
            UpdateAccount();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private async void OnImportOpml(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".opml"); picker.FileTypeFilter.Add(".xml");
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            if ((await file.GetBasicPropertiesAsync()).Size > 10 * 1024 * 1024) throw new FormatException("Choose an OPML file smaller than 10 MB.");
            using var stream = File.OpenRead(file.Path);
            var imported = Opml.Parse(stream);
            if (imported.Subscriptions.Count == 0 && imported.Folders.Count == 0) throw new FormatException("No feeds or folders were found in this OPML file.");
            await SwitchToLocalAsync();
            var added = SidebarOrganization.Import(_library, imported);
            await _localStore.SaveAsync(_library);
            BuildFeedNavigation(); ShowLocalArticles();
            var summary = $"Added {added} feed{(added == 1 ? "" : "s")}; {imported.Subscriptions.Count - added} already subscribed." + (imported.Skipped > 0 ? $" Skipped {imported.Skipped} invalid feed URLs." : "");
            if (imported.Subscriptions.Count > 0) await RefreshLocalAsync(summary);
            else ShowNotice("OPML imported", $"Imported {imported.Folders.Count} folders.", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowNotice("Could not import OPML", ex is System.Xml.XmlException ? "This file contains invalid XML. Export a new OPML subscription list and try again." : ex.Message, InfoBarSeverity.Warning); }
    }

    private async void OnExportOpml(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            // Feedly uses feed/<URL> identifiers; export these without changing that account.
            var feeds = _library.Subscriptions.Select(s => !string.IsNullOrEmpty(s.FeedUrl) ? s : s.Id.StartsWith("feed/", StringComparison.Ordinal) && ArticleText.TryWebUri(s.Id[5..], out _) ? s with { FeedUrl = s.Id[5..] } : s).Where(s => s.FeedUrl.Length > 0).ToList();
            if (feeds.Count == 0 && _library.Folders.Count == 0) { ShowNotice("No feeds or folders to export", "Add a feed or folder first. Saved article links stay in your library.", InfoBarSeverity.Informational); return; }
            var picker = new FileSavePicker { SuggestedFileName = "Unread subscriptions" };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeChoices.Add("OPML subscriptions", new List<string> { ".opml" });
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            var exported = SidebarOrganization.CopyForEdit(_library); exported.Subscriptions = feeds;
            await Windows.Storage.FileIO.WriteTextAsync(file, Opml.Export(exported));
            ShowNotice("OPML exported", $"Saved {feeds.Count} subscriptions to {file.Name}.", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowNotice("Could not export OPML", ex.Message, InfoBarSeverity.Warning); }
    }

    private async Task RemoveFeedAsync(string id)
    {
        if (_busy || !_local) return;
        try
        {
            var feed = _library.Subscriptions.FirstOrDefault(s => s.Id == id);
            if (feed is null) return;
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme, Title = "Remove " + feed.Title + "?", Content = "This stops updates and removes its cached articles. Saved articles will be kept.", PrimaryButtonText = "Remove feed", CloseButtonText = "Cancel" };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            _library.Subscriptions.Remove(feed); _library.Articles.RemoveAll(a => a.FeedId == id && !a.IsSaved);
            await _localStore.SaveAsync(_library); ResetLibraryView();
        }
        catch (Exception ex) { ShowError(ex); }
    }
}
