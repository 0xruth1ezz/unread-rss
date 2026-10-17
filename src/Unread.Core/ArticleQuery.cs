namespace Unread.Core;

public static class ArticleQuery
{
    public static IEnumerable<Article> Apply(LibrarySnapshot library, string source, bool unreadOnly, string search = "")
    {
        IEnumerable<Article> articles = library.Articles;
        if (source == "saved") articles = articles.Where(a => a.IsSaved);
        else if (source != "all")
        {
            var feedIds = SidebarOrganization.FeedIds(library, source);
            articles = articles.Where(a => feedIds.Contains(a.FeedId));
        }
        if (unreadOnly) articles = articles.Where(a => a.IsUnread);
        var query = search.Trim();
        if (query.Length > 0)
            articles = articles.Where(a => a.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || a.FeedTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase) || a.Summary.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        return articles.OrderByDescending(a => a.Published);
    }
}
