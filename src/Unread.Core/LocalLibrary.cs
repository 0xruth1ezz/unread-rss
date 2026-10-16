using System.Security.Cryptography;
using System.Text;

namespace Unread.Core;

public static class LocalLibrary
{
    public const string SavedLinksId = "local:saved-links";

    public static string WebUrl(string value)
    {
        if (!ArticleText.TryWebUri(value.Trim(), out var uri) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new FormatException("Enter a complete http:// or https:// URL without a username or password.");
        return new UriBuilder(uri) { Fragment = "" }.Uri.AbsoluteUri;
    }

    public static string Id(string kind, string value) => "local:" + kind + ":" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static Subscription Subscription(string url, string? title = null, IEnumerable<FeedCategory>? categories = null, string siteUrl = "")
    {
        url = WebUrl(url);
        return new(Id("feed", url), string.IsNullOrWhiteSpace(title) ? new Uri(url).Host : title.Trim(), categories?.ToList() ?? [])
        { FeedUrl = url, SiteUrl = siteUrl };
    }

    public static FeedCategory Folder(IEnumerable<string> path)
    {
        var parts = path.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        return new(Id("folder", System.Text.Json.JsonSerializer.Serialize(parts)), string.Join(" / ", parts)) { Path = parts };
    }

    public static int AddSubscriptions(LibrarySnapshot library, IEnumerable<Subscription> subscriptions)
    {
        var added = 0;
        foreach (var feed in subscriptions)
        {
            var index = library.Subscriptions.FindIndex(s => s.Id == feed.Id || s.FeedUrl == feed.FeedUrl);
            if (index < 0) { library.Subscriptions.Add(feed); added++; }
            else
            {
                var existing = library.Subscriptions[index];
                library.Subscriptions[index] = existing with
                { Categories = existing.Categories.Concat(feed.Categories).DistinctBy(c => c.Id).ToList() };
            }
        }
        return added;
    }

    public static void MergeArticles(LibrarySnapshot library, IEnumerable<Article> incoming)
    {
        var articles = library.Articles.ToDictionary(a => a.Id);
        foreach (var article in incoming)
        {
            if (articles.TryGetValue(article.Id, out var previous))
            {
                article.IsUnread = previous.IsUnread;
                article.IsSaved = previous.IsSaved;
                if (article.Published == default) article.Published = previous.Published;
            }
            if (article.Published == default) article.Published = DateTimeOffset.UtcNow;
            articles[article.Id] = article;
        }
        // Saved links and bookmarks survive the rolling feed cache.
        library.Articles = articles.Values.OrderByDescending(a => a.Published)
            .Where((a, index) => index < 2000 || a.IsSaved || a.FeedId == SavedLinksId).ToList();
    }
}
