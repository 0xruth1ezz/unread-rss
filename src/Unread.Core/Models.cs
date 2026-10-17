using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Unread.Core;

public sealed class Article : INotifyPropertyChanged
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "Untitled article";
    public string FeedId { get; set; } = "";
    public string FeedTitle { get; set; } = "";
    public string Author { get; set; } = "";
    public string Url { get; set; } = "";
    public string Html { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public DateTimeOffset Published { get; set; }
    private bool _isUnread = true;
    public bool IsUnread { get => _isUnread; set { _isUnread = value; Changed(); Changed(nameof(ReadOpacity)); } }
    private bool _isSaved;
    public bool IsSaved { get => _isSaved; set { _isSaved = value; Changed(); } }
    [JsonIgnore] public string SourceLabel => FeedTitle.ToUpperInvariant();
    [JsonIgnore] public string DateLabel => Published.ToLocalTime().Date == DateTimeOffset.Now.Date
        ? Published.ToLocalTime().ToString("h:mm tt") : Published.ToLocalTime().ToString("MMM d").ToUpperInvariant();
    [JsonIgnore] public string Summary => ArticleText.PlainText(Html);
    [JsonIgnore] public double ReadOpacity => IsUnread ? 1 : 0.6;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed record FeedCategory(string Id, string Label)
{
    public string[] Path { get; init; } = [];
}
public sealed record Subscription(string Id, string Title, List<FeedCategory> Categories)
{
    public string FeedUrl { get; init; } = "";
    public string SiteUrl { get; init; } = "";
}
public sealed record FeedProfile(string Id, string Name);
public sealed record StreamPage(List<Article> Items, string? Continuation);
public sealed record PendingAction(string ArticleId, string Kind, bool Value);

public sealed class LibrarySnapshot
{
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public List<Subscription> Subscriptions { get; set; } = [];
    public List<Article> Articles { get; set; } = [];
    public Dictionary<string, int> UnreadCounts { get; set; } = [];
    public List<PendingAction> Pending { get; set; } = [];
    public DateTimeOffset? LastSync { get; set; }
}

public sealed class ReaderSettings
{
    public string Library { get; set; } = "local";
    public string Theme { get; set; } = "Paper";
    public double FontSize { get; set; } = 20;
    public bool MarkReadOnOpen { get; set; } = true;
    public double SidebarWidth { get; set; } = PaneLayout.DefaultSidebarWidth;
    public double ArticleListWidth { get; set; } = PaneLayout.DefaultArticleListWidth;
}
