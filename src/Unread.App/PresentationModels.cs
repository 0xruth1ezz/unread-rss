using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.ComponentModel;
using Unread.Core;

namespace Unread.App;

internal static class Palette
{
    public static SolidColorBrush Brush(string key) => (SolidColorBrush)Application.Current.Resources[key];
}

public sealed class ArticleRow(Article article) : INotifyPropertyChanged
{
    public Article Article { get; } = article;
    private bool _selected;
    public bool Selected { get => _selected; set { _selected = value; Refresh(); } }
    public Brush Background => Selected ? Palette.Brush("ReaderActiveBackground") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    public Brush Foreground => Selected ? new SolidColorBrush(Microsoft.UI.Colors.White) : Palette.Brush("ReaderForeground");
    public Brush SourceForeground => Selected ? new SolidColorBrush(Microsoft.UI.Colors.White) : Palette.Brush("ReaderAccent");
    public Brush SecondaryForeground => Selected ? new SolidColorBrush(Microsoft.UI.Colors.White) : Palette.Brush("ReaderSecondary");
    public Brush Divider => Selected ? Palette.Brush("ReaderActiveBackground") : Palette.Brush("ReaderDivider");
    public string AccessibleName => $"{Article.Title}, {Article.FeedTitle}, {(Article.IsUnread ? "unread" : "read")}{(Article.IsSaved ? ", saved" : "")}";
    public Visibility ImageVisibility => Uri.TryCreate(Article.ImageUrl, UriKind.Absolute, out var uri) &&
        (uri.Scheme is "https" or "http" or "ms-appx") ? Visibility.Visible : Visibility.Collapsed;
    public BitmapImage? Image => ImageVisibility == Visibility.Visible ? new(new Uri(Article.ImageUrl)) : null;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh() => PropertyChanged?.Invoke(this, new(null));
}

public sealed class NavigationItem(string title, string glyph, string key) : INotifyPropertyChanged
{
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    public string Key { get; } = key;
    public string CountLabel { get; set; } = "";
    public bool Selected { get; set; }
    public Brush Background => Selected ? Palette.Brush("ReaderSelection") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh() => PropertyChanged?.Invoke(this, new(null));
}
