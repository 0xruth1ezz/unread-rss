using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Unread.Core;
using Windows.ApplicationModel.DataTransfer;

namespace Unread.App;

public sealed partial class MainWindow
{
    private const string SidebarDragFormat = "Unread.SidebarItem";
    private sealed record SidebarDrag(SidebarEntry Entry, LibrarySnapshot Library, string Token);
    private sealed record FolderVisual(StackPanel Children, FontIcon Chevron, Button Toggle);
    private SidebarDrag? _sidebarDrag;
    private bool _sidebarDragStarting;
    private readonly Dictionary<string, FolderVisual> _folderVisuals = [];
    private Border? _dropIndicator;
    private DispatcherTimer? _sidebarDragTimer;
    private int _sidebarScrollDirection;
    private string? _hoverFolder;
    private DateTimeOffset _hoverStarted;

    private void RebuildSidebar()
    {
        EndSidebarDrag();
        if (!_local && !_demo) _library.Folders.Clear();
        SidebarOrganization.Normalize(_library);
        FeedNavigation.Children.Clear(); _folderVisuals.Clear(); _localUnreadCounts.Clear();
        NewFolderButton.IsEnabled = !_busy && (_local || _demo);
        ToolTipService.SetToolTip(NewFolderButton, _local || _demo ? "New folder" : "Folder management is available in the local library");
        if (_demo || _local)
            foreach (var group in _library.Articles.Where(a => a.IsUnread).GroupBy(a => a.FeedId)) _localUnreadCounts[group.Key] = group.Count();
        foreach (var folder in _library.Folders)
            _localUnreadCounts[folder.Id] = SidebarOrganization.FeedIds(_library, folder.Id)
                .Sum(id => (_demo || _local ? _localUnreadCounts : _library.UnreadCounts).GetValueOrDefault(id));
        RenderSidebarChildren(FeedNavigation, "", 0);
        if (_library.Subscriptions.Count == 0 && _library.Folders.Count == 0)
            FeedNavigation.Children.Add(new TextBlock { Text = _local ? "Add a feed or create a folder to begin." : "Your subscriptions will appear here.", TextWrapping = TextWrapping.Wrap, Margin = new(12), FontSize = 13, Foreground = Palette.Brush("ReaderSecondary") });
        if (_local && _library.Articles.Any(a => a.FeedId == LocalLibrary.SavedLinksId))
            FeedNavigation.Children.Add(FeedButton("Saved Links", LocalLibrary.SavedLinksId, "\uE71B"));
    }

    private void RenderSidebarChildren(StackPanel panel, string parentId, int depth)
    {
        foreach (var entry in SidebarOrganization.Children(_library, parentId))
        {
            var button = FeedButton(entry.Title, entry.Id, entry.IsFolder ? "\uE8B7" : "\uE8F3",
                entry.IsFolder ? _library.Folders.First(f => f.Id == entry.Id).Label : null);
            if (entry.IsFolder && button.Content is Grid content && content.Children[2] is TextBlock count)
                count.Text = _localUnreadCounts[entry.Id] is var unread && unread > 0 ? unread.ToString() : "";
            var row = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
            if (entry.IsFolder)
            {
                row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
                var chevron = new FontIcon { FontSize = 9, Foreground = Palette.Brush("ReaderSecondary") };
                var toggle = new Button { Content = chevron, Width = 26, Padding = new(4), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new(0) };
                row.Children.Add(toggle); Grid.SetColumn(button, 1);
                var children = new StackPanel { Spacing = 2, Margin = new(depth < 4 ? 12 : 0, 0, 0, 0) };
                _folderVisuals[entry.Id] = new(children, chevron, toggle);
                SetFolderExpanded(entry.Id, !_collapsedFolders.Contains(entry.Id));
                toggle.Click += (_, _) => SetFolderExpanded(entry.Id, _collapsedFolders.Contains(entry.Id));
                RenderSidebarChildren(children, entry.Id, depth + 1);
                var section = new StackPanel { Spacing = 2, Margin = new(0, 2, 0, 4) };
                section.Children.Add(row); section.Children.Add(children); panel.Children.Add(section);
            }
            else panel.Children.Add(row);
            row.Children.Add(button);
            var indicator = new Border { BorderBrush = Palette.Brush("ReaderAccent"), IsHitTestVisible = false, CornerRadius = new(5) };
            Grid.SetColumnSpan(indicator, 2); row.Children.Add(indicator);
            ToolTipService.SetToolTip(button, entry.IsFolder ? _library.Folders.First(f => f.Id == entry.Id).Label : entry.Title);
            if (_local || _demo)
            {
                button.ContextFlyout = SidebarEntryMenu(entry);
                AutomationProperties.SetHelpText(button, "Drag to reorder or move into a folder. The context menu also offers Move up, Move down and Move to.");
                AttachSidebarDrag(button, row, indicator, entry);
            }
        }
    }

    private void SetFolderExpanded(string id, bool expanded)
    {
        if (!_folderVisuals.TryGetValue(id, out var visual)) return;
        if (expanded) _collapsedFolders.Remove(id); else _collapsedFolders.Add(id);
        visual.Children.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        visual.Chevron.Glyph = expanded ? "\uE70D" : "\uE76C";
        AutomationProperties.SetName(visual.Toggle, (expanded ? "Collapse " : "Expand ") + _library.Folders.First(f => f.Id == id).Label + " folder");
    }

    private MenuFlyout SidebarEntryMenu(SidebarEntry entry)
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) => PopulateSidebarEntryMenu(menu, entry);
        return menu;
    }

    private void PopulateSidebarEntryMenu(MenuFlyout menu, SidebarEntry entry)
    {
        menu.Items.Clear();
        void Add(string text, Func<Task> action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
            item.Click += async (_, _) => await action(); menu.Items.Add(item);
        }
        if (entry.IsFolder)
        {
            Add("New subfolder…", () => FolderDialogAsync(null, entry.Id));
            Add("Rename folder…", () => FolderDialogAsync(entry.Id, entry.ParentId));
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        var siblings = SidebarOrganization.Children(_library, entry.ParentId).ToList();
        var index = siblings.FindIndex(e => e.Id == entry.Id);
        Add("Move up", () => EditSidebarAsync(l => SidebarOrganization.Move(l, entry.Id, entry.ParentId, entry.ParentId, siblings[index - 1].Id)), index > 0);
        Add("Move down", () => EditSidebarAsync(l => SidebarOrganization.Move(l, entry.Id, entry.ParentId, entry.ParentId, siblings[index + 1].Id, true)), index >= 0 && index < siblings.Count - 1);
        var moveTo = new MenuFlyoutSubItem { Text = "Move to" };
        void Destination(string title, string parentId)
        {
            var item = new MenuFlyoutItem { Text = title, IsEnabled = parentId != entry.ParentId };
            item.Click += async (_, _) => await EditSidebarAsync(l => SidebarOrganization.Move(l, entry.Id, entry.ParentId, parentId), parentId);
            moveTo.Items.Add(item);
        }
        Destination("Top level", "");
        var descendants = entry.IsFolder ? DescendantFolderIds(entry.Id) : [];
        foreach (var folder in _library.Folders.Where(f => !descendants.Contains(f.Id)).OrderBy(f => f.Label, StringComparer.CurrentCultureIgnoreCase)) Destination(folder.Label, folder.Id);
        menu.Items.Add(moveTo); menu.Items.Add(new MenuFlyoutSeparator());
        if (entry.IsFolder) Add("Remove folder…", () => RemoveFolderAsync(entry.Id));
        else if (_local) Add("Remove feed…", () => RemoveFeedAsync(entry.Id));
    }

    private HashSet<string> DescendantFolderIds(string id)
    {
        var result = new HashSet<string> { id };
        void Visit(string parent) { foreach (var child in SidebarOrganization.Children(_library, parent).Where(e => e.IsFolder)) { result.Add(child.Id); Visit(child.Id); } }
        Visit(id); return result;
    }

    private async void OnNewFolder(object sender, RoutedEventArgs e) => await FolderDialogAsync(null, "");

    private async Task FolderDialogAsync(string? id, string parentId)
    {
        if (_busy || (!_local && !_demo)) return;
        var originalLibrary = _library;
        var existing = id is null ? null : _library.Folders.FirstOrDefault(f => f.Id == id);
        if (id is not null && existing is null) return;
        var name = new TextBox { Header = "Folder name", Text = existing?.Path[^1] ?? "", MaxLength = 100, MinWidth = 300 };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Palette.Brush("ReaderAccent"), Visibility = Visibility.Collapsed };
        var content = new StackPanel { Spacing = 16, MaxWidth = 440 };
        content.Children.Add(new TextBlock { Text = "In " + (parentId.Length == 0 ? "Subscriptions" : _library.Folders.First(f => f.Id == parentId).Label), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(name); content.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme, Title = id is null ? "New folder" : "Rename folder", Content = content, PrimaryButtonText = id is null ? "Create folder" : "Rename", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        dialog.Opened += (_, _) => { name.Focus(FocusState.Programmatic); name.SelectAll(); };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true; var deferral = args.GetDeferral(); dialog.IsPrimaryButtonEnabled = false;
            try
            {
                if (!ReferenceEquals(_library, originalLibrary)) throw new InvalidOperationException("The library changed. Close this dialog and try again.");
                string? created = null;
                var changed = await EditSidebarAsync(l => { if (id is null) created = SidebarOrganization.CreateFolder(l, name.Text, parentId).Id; else SidebarOrganization.RenameFolder(l, id, name.Text); return true; }, parentId, reportError: false);
                if (changed) { args.Cancel = false; if (created is not null) _collapsedFolders.Remove(created); }
            }
            catch (Exception ex) { error.Text = ex.Message; error.Visibility = Visibility.Visible; }
            finally { dialog.IsPrimaryButtonEnabled = true; deferral.Complete(); }
        };
        try { await dialog.ShowAsync(); } catch (Exception ex) { ShowError(ex); }
    }

    private async Task RemoveFolderAsync(string id)
    {
        if (_busy || (!_local && !_demo) || _library.Folders.FirstOrDefault(f => f.Id == id) is not { } folder) return;
        var originalLibrary = _library;
        var parent = folder.Path.Length == 1 ? "the top level" : string.Join(" / ", folder.Path[..^1]);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme, Title = "Remove " + folder.Path[^1] + "?", Content = $"This removes the folder and its subfolders. Feeds stay subscribed in {parent} or their other folders. All articles are kept.", PrimaryButtonText = "Remove folder", CloseButtonText = "Cancel" };
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && ReferenceEquals(originalLibrary, _library))
                await EditSidebarAsync(l => { SidebarOrganization.RemoveFolder(l, id); return true; });
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task<bool> EditSidebarAsync(Func<LibrarySnapshot, bool> edit, string? expandFolder = null, bool reportError = true)
    {
        if (_busy || (!_local && !_demo)) return false;
        var originalLibrary = _library;
        await _accountGate.WaitAsync();
        try
        {
            if (_busy || !ReferenceEquals(originalLibrary, _library)) return false;
            SetBusy(true);
            var before = SidebarOrganization.FeedIds(_library, _view);
            var edited = SidebarOrganization.CopyForEdit(_library);
            if (!edit(edited)) return false;
            if (_local) await _localStore.SaveAsync(edited);
            _library = edited;
            if (expandFolder is not null) _collapsedFolders.Remove(expandFolder);
            var validView = _view is "unread" or "all" or "saved" || _view == LocalLibrary.SavedLinksId
                || _library.Subscriptions.Any(s => s.Id == _view) || _library.Folders.Any(f => f.Id == _view);
            if (!validView) { _view = "unread"; _viewLabel = "Unread Articles"; SelectUnreadNavigation(); }
            if (_library.Folders.FirstOrDefault(f => f.Id == _view) is { } folder) _viewLabel = folder.Label;
            ViewTitle.Text = _viewLabel;
            BuildFeedNavigation();
            if (!validView || !before.SetEquals(SidebarOrganization.FeedIds(_library, _view))) ShowLocalArticles();
            return true;
        }
        catch (Exception ex) { if (!reportError) throw; ShowError(ex); return false; }
        finally { SetBusy(false); _accountGate.Release(); }
    }

    private void AttachSidebarDrag(Button source, Grid row, Border indicator, SidebarEntry entry)
    {
        row.AllowDrop = true;
        PointerRoutedEventArgs? candidate = null;
        Windows.Foundation.Point? origin = null;
        async Task StartAsync()
        {
            if (candidate is not { } pointer || _busy || _sidebarDragStarting) return;
            var point = pointer.GetCurrentPoint(source);
            candidate = null; origin = null; _sidebarDragStarting = true;
            // ButtonBase handles pointer presses. Observe handled events explicitly, then cancel
            // its pressed state before starting the native operation so a drag never navigates.
            source.ReleasePointerCapture(pointer.Pointer);
            try { await source.StartDragAsync(point); }
            catch (Exception ex) { ShowError(ex); }
            finally { _sidebarDragStarting = false; EndSidebarDrag(); }
        }
        source.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
        {
            candidate = null; origin = null;
            if (_busy || _sidebarDragStarting) return;
            var point = args.GetCurrentPoint(source);
            if (!point.Properties.IsLeftButtonPressed && args.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Touch) return;
            candidate = args; origin = point.Position;
        }), true);
        source.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(async (_, args) =>
        {
            if (candidate?.Pointer.PointerId != args.Pointer.PointerId || origin is not { } start) return;
            var point = args.GetCurrentPoint(source);
            candidate = args;
            if (args.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch) return; // Touch scrolls; hold to drag.
            if (!point.Properties.IsLeftButtonPressed) { candidate = null; origin = null; return; }
            if (Math.Abs(point.Position.X - start.X) < 6 && Math.Abs(point.Position.Y - start.Y) < 6) return;
            args.Handled = true; await StartAsync();
        }), true);
        source.AddHandler(UIElement.HoldingEvent, new HoldingEventHandler(async (_, args) =>
        {
            if (args.HoldingState != Microsoft.UI.Input.HoldingState.Started || candidate is null) return;
            args.Handled = true; await StartAsync();
        }), true);
        PointerEventHandler clearCandidate = (_, _) => { candidate = null; origin = null; };
        source.AddHandler(UIElement.PointerReleasedEvent, clearCandidate, true);
        source.AddHandler(UIElement.PointerCanceledEvent, clearCandidate, true);
        source.AddHandler(UIElement.PointerCaptureLostEvent, clearCandidate, true);
        source.DragStarting += (_, args) =>
        {
            if (_busy) { args.Cancel = true; return; }
            var token = Guid.NewGuid().ToString("N"); _sidebarDrag = new(entry, _library, token);
            args.Data.SetData(SidebarDragFormat, token); args.Data.Properties[SidebarDragFormat] = token;
            args.Data.RequestedOperation = DataPackageOperation.Move; args.AllowedOperations = DataPackageOperation.Move;
            SidebarRootDropTarget.Visibility = Visibility.Visible;
            _sidebarDragTimer ??= CreateSidebarDragTimer(); _sidebarDragTimer.Start();
        };
        source.DropCompleted += (_, _) => EndSidebarDrag();
        row.DragLeave += (_, _) => { ClearDropIndicator(); _hoverFolder = null; _sidebarScrollDirection = 0; };
        row.DragOver += (_, args) =>
        {
            if (!IsSidebarDrag(args)) return;
            var placement = SidebarDropPlacement(row, entry, args);
            if (!CanDropSidebar(entry, placement)) { args.AcceptedOperation = DataPackageOperation.None; args.Handled = true; return; }
            args.AcceptedOperation = DataPackageOperation.Move; args.Handled = true;
            args.DragUIOverride.Caption = placement == 0 ? "Move into " + entry.Title : "Move " + (placement < 0 ? "before " : "after ") + entry.Title;
            args.DragUIOverride.IsCaptionVisible = true;
            ClearDropIndicator(); _dropIndicator = indicator;
            indicator.BorderThickness = placement == 0 ? new(1) : placement < 0 ? new(0, 2, 0, 0) : new(0, 0, 0, 2);
            UpdateSidebarDragScroll(args);
            var hover = placement == 0 ? entry.Id : null;
            if (_hoverFolder != hover) { _hoverFolder = hover; _hoverStarted = DateTimeOffset.UtcNow; }
        };
        row.Drop += async (_, args) =>
        {
            if (!IsSidebarDrag(args) || _sidebarDrag is not { } drag) return;
            args.Handled = true; var placement = SidebarDropPlacement(row, entry, args);
            var accepted = CanDropSidebar(entry, placement); EndSidebarDrag();
            if (!accepted) { args.AcceptedOperation = DataPackageOperation.None; return; }
            var parent = placement == 0 ? entry.Id : entry.ParentId;
            var deferral = args.GetDeferral();
            try { args.AcceptedOperation = await EditSidebarAsync(l => SidebarOrganization.Move(l, drag.Entry.Id, drag.Entry.ParentId, parent, placement == 0 ? null : entry.Id, placement > 0), parent) ? DataPackageOperation.Move : DataPackageOperation.None; }
            finally { deferral.Complete(); }
        };
    }

    private bool IsSidebarDrag(DragEventArgs args) => !_busy && _sidebarDrag is { } drag && ReferenceEquals(drag.Library, _library)
        && args.DataView.Properties.TryGetValue(SidebarDragFormat, out var value) && value is string token && token == drag.Token;

    private static int SidebarDropPlacement(FrameworkElement row, SidebarEntry entry, DragEventArgs args)
    {
        var fraction = args.GetPosition(row).Y / Math.Max(1, row.ActualHeight);
        return entry.IsFolder && fraction is >= 0.25 and <= 0.75 ? 0 : fraction < 0.5 ? -1 : 1;
    }

    private bool CanDropSidebar(SidebarEntry target, int placement)
    {
        if (_sidebarDrag is not { } drag || drag.Entry.Id == target.Id) return false;
        var parent = placement == 0 ? target.Id : target.ParentId;
        return !drag.Entry.IsFolder || !DescendantFolderIds(drag.Entry.Id).Contains(parent);
    }

    private void OnSidebarRootDragOver(object sender, DragEventArgs args)
    {
        if (args.Handled || !IsSidebarDrag(args)) return;
        args.AcceptedOperation = DataPackageOperation.Move; args.Handled = true;
        args.DragUIOverride.Caption = "Move to top level"; args.DragUIOverride.IsCaptionVisible = true;
        ClearDropIndicator(); _hoverFolder = null; SidebarRootDropTarget.BorderBrush = Palette.Brush("ReaderAccent");
        UpdateSidebarDragScroll(args);
    }
    private void OnSidebarRootDragLeave(object sender, DragEventArgs args)
    { ClearDropIndicator(); _hoverFolder = null; _sidebarScrollDirection = 0; }
    private void OnSidebarViewportDragLeave(object sender, DragEventArgs args)
    {
        var position = args.GetPosition(SidebarScroll);
        if (position.X < 0 || position.Y < 0 || position.X > SidebarScroll.ActualWidth || position.Y > SidebarScroll.ActualHeight)
            OnSidebarRootDragLeave(sender, args);
    }
    private async void OnSidebarRootDrop(object sender, DragEventArgs args)
    {
        if (args.Handled || !IsSidebarDrag(args) || _sidebarDrag is not { } drag) return;
        args.Handled = true; EndSidebarDrag(); var deferral = args.GetDeferral();
        try { args.AcceptedOperation = await EditSidebarAsync(l => SidebarOrganization.Move(l, drag.Entry.Id, drag.Entry.ParentId, "")) ? DataPackageOperation.Move : DataPackageOperation.None; }
        finally { deferral.Complete(); }
    }
    private void UpdateSidebarDragScroll(DragEventArgs args)
    {
        var y = args.GetPosition(SidebarScroll).Y;
        _sidebarScrollDirection = y < 40 ? -1 : y > SidebarScroll.ActualHeight - 40 ? 1 : 0;
    }
    private DispatcherTimer CreateSidebarDragTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            if (_sidebarDrag is null) { timer.Stop(); return; }
            if (_sidebarScrollDirection != 0) SidebarScroll.ChangeView(null, Math.Clamp(SidebarScroll.VerticalOffset + _sidebarScrollDirection * 18, 0, SidebarScroll.ScrollableHeight), null, true);
            if (_hoverFolder is { } folder && DateTimeOffset.UtcNow - _hoverStarted > TimeSpan.FromMilliseconds(600)) SetFolderExpanded(folder, true);
        };
        return timer;
    }
    private void ClearDropIndicator()
    {
        if (_dropIndicator is not null) _dropIndicator.BorderThickness = new(0);
        _dropIndicator = null; SidebarRootDropTarget.BorderBrush = Palette.Brush("ReaderDivider");
    }
    private void EndSidebarDrag()
    {
        _sidebarDrag = null; _hoverFolder = null; _sidebarScrollDirection = 0; _sidebarDragTimer?.Stop();
        ClearDropIndicator(); SidebarRootDropTarget.Visibility = Visibility.Collapsed;
    }
}
