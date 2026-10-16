namespace Unread.Core;

/// <summary>Persists each intent before sending it; interrupted syncs replay safely.</summary>
public sealed class SyncEngine(FeedlyClient client, LibraryStore store)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task ChangeAsync(LibrarySnapshot library, Article article, string kind, bool value)
    {
        await _gate.WaitAsync();
        try
        {
            library.Pending.RemoveAll(p => p.ArticleId == article.Id && p.Kind == kind);
            library.Pending.Add(new(article.Id, kind, value));
            if (kind == "read")
            {
                var delta = article.IsUnread == value ? (value ? -1 : 1) : 0;
                article.IsUnread = !value;
                var keys = new[] { article.FeedId, $"user/{library.UserId}/category/global.all" }
                    .Concat(library.Subscriptions.FirstOrDefault(s => s.Id == article.FeedId)?.Categories.Select(c => c.Id) ?? []);
                foreach (var key in keys.Distinct())
                    if (library.UnreadCounts.ContainsKey(key)) library.UnreadCounts[key] = Math.Max(0, library.UnreadCounts[key] + delta);
            }
            if (kind == "saved") article.IsSaved = value;
            await store.SaveAsync(library);
        }
        finally { _gate.Release(); }
    }

    public async Task FlushAsync(LibrarySnapshot library, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            foreach (var action in library.Pending.ToList())
            {
                await client.ApplyAsync(library.UserId, action, ct);
                library.Pending.Remove(action);
                await store.SaveAsync(library);
            }
        }
        finally { _gate.Release(); }
    }

    public static void Merge(LibrarySnapshot library, IEnumerable<Article> incoming)
    {
        foreach (var article in incoming)
        {
            foreach (var action in library.Pending.Where(p => p.ArticleId == article.Id))
            {
                if (action.Kind == "read") article.IsUnread = !action.Value;
                if (action.Kind == "saved") article.IsSaved = action.Value;
            }
            var old = library.Articles.FindIndex(a => a.Id == article.Id);
            if (old >= 0) library.Articles[old] = article;
            else library.Articles.Add(article);
        }
        // Keep saved articles and articles with unsent actions even when trimming the cache.
        var keep = library.Articles.OrderByDescending(a => a.Published).Take(2000).Select(a => a.Id).ToHashSet();
        library.Articles.RemoveAll(a => !keep.Contains(a.Id) && !a.IsSaved && !library.Pending.Any(p => p.ArticleId == a.Id));
    }
}
