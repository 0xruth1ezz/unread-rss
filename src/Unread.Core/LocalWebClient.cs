using System.Net;
using System.Text;
using HtmlAgilityPack;

namespace Unread.Core;

public sealed record FeedCandidate(string Title, string Url);
public sealed record FeedDiscovery(List<FeedCandidate> Feeds, ParsedFeed? Feed);

public sealed class LocalWebClient(HttpClient http, TimeSpan? downloadTimeout = null)
{
    private const int MaxBytes = 8 * 1024 * 1024;

    private async Task<(string Text, Uri Uri)> DownloadAsync(string url, CancellationToken ct)
    {
        // ResponseHeadersRead ends HttpClient.Timeout at the headers. Keep the body bounded too.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(downloadTimeout ?? TimeSpan.FromSeconds(25));
        ct = deadline.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, LocalLibrary.WebUrl(url));
        request.Headers.UserAgent.ParseAdd("UnreadReader/1.0");
        request.Headers.Accept.ParseAdd("application/atom+xml, application/rss+xml, application/xml, text/html;q=0.9, */*;q=0.5");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxBytes) throw new FormatException("This page or feed is larger than the 8 MB download limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int count;
        while ((count = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > MaxBytes) throw new FormatException("This page or feed is larger than the 8 MB download limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, count), ct);
        }
        var bytes = buffer.ToArray();
        var encoding = Encoding.UTF8;
        var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"', '\'');
        if (!string.IsNullOrWhiteSpace(charset))
            try { encoding = Encoding.GetEncoding(charset); } catch (ArgumentException) { }
        // XML declarations and BOMs matter for OPML-era feeds without an HTTP charset.
        if (string.IsNullOrWhiteSpace(charset))
        {
            var prefix = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 200));
            var match = System.Text.RegularExpressions.Regex.Match(prefix, "<\\?xml[^>]*encoding\\s*=\\s*['\"]([^'\"]+)['\"]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success) try { encoding = Encoding.GetEncoding(match.Groups[1].Value); } catch (ArgumentException) { }
        }
        using var reader = new StreamReader(new MemoryStream(bytes), encoding, true);
        return (await reader.ReadToEndAsync(ct), response.RequestMessage?.RequestUri ?? new Uri(url));
    }

    public async Task<ParsedFeed> FetchFeedAsync(Subscription subscription, CancellationToken ct = default)
    {
        var page = await DownloadAsync(subscription.FeedUrl, ct);
        return FeedParser.Parse(page.Text, subscription, page.Uri);
    }

    public async Task<FeedDiscovery> DiscoverAsync(string url, CancellationToken ct = default)
    {
        url = LocalLibrary.WebUrl(url);
        var page = await DownloadAsync(url, ct);
        try
        {
            var feed = FeedParser.Parse(page.Text, LocalLibrary.Subscription(url), page.Uri);
            return new([new(feed.Title, url)], feed);
        }
        catch (System.Xml.XmlException) { }
        catch (FormatException) { }
        var document = ArticleText.Parse(page.Text);
        var baseUri = HtmlBase(document, page.Uri);
        var candidates = new List<FeedCandidate>();
        foreach (var link in document.DocumentNode.Descendants("link"))
        {
            var type = link.GetAttributeValue("type", "").Split(';')[0].Trim();
            if (!link.GetAttributeValue("rel", "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("alternate", StringComparer.OrdinalIgnoreCase) ||
                !(type.Equals("application/rss+xml", StringComparison.OrdinalIgnoreCase) || type.Equals("application/atom+xml", StringComparison.OrdinalIgnoreCase) || type.Equals("application/rdf+xml", StringComparison.OrdinalIgnoreCase))) continue;
            var href = Absolute(baseUri, link.GetAttributeValue("href", ""));
            if (href.Length > 0) candidates.Add(new(WebUtility.HtmlDecode(link.GetAttributeValue("title", "RSS feed")), href));
        }
        return new(candidates.DistinctBy(f => f.Url).Take(30).ToList(), null);
    }

    public async Task<Article> FetchArticleAsync(string url, CancellationToken ct = default)
    {
        url = LocalLibrary.WebUrl(url);
        var page = await DownloadAsync(url, ct);
        var document = ArticleText.Parse(page.Text);
        var root = document.DocumentNode;
        string Meta(params string[] names) => root.Descendants("meta").FirstOrDefault(m => names.Contains(m.GetAttributeValue("property", m.GetAttributeValue("name", "")), StringComparer.OrdinalIgnoreCase))?.GetAttributeValue("content", "") ?? "";
        var title = Meta("og:title", "twitter:title");
        if (string.IsNullOrWhiteSpace(title)) title = root.Descendants("title").FirstOrDefault()?.InnerText ?? new Uri(url).Host;
        var author = Meta("author", "article:author");
        var description = Meta("og:description", "description");
        var published = DateTimeOffset.TryParse(Meta("article:published_time", "datePublished"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var date) ? date : DateTimeOffset.UtcNow;
        var content = root.Descendants("article").OrderByDescending(n => n.InnerText.Length).FirstOrDefault()
            ?? root.Descendants("main").FirstOrDefault();
        if (content is null)
        {
            content = root.Descendants().Where(n => n.Name is "div" or "section")
                .Where(n => n.Descendants("p").Count() >= 2)
                .OrderByDescending(n => n.Descendants("p").Sum(p => p.InnerText.Length) - n.Descendants("a").Sum(a => a.InnerText.Length))
                .FirstOrDefault();
        }
        if (content is not null)
        {
            foreach (var node in content.Descendants().Where(n => n.Name is "nav" or "footer" or "header" or "aside" or "button" or "input" or "select" or "textarea").ToList()) node.Remove();
            // The native reader supplies its own title.
            foreach (var heading in content.Descendants("h1").ToList()) heading.Remove();
        }
        var html = content?.InnerHtml ?? "<p>" + WebUtility.HtmlEncode(description.Length > 0 ? description : "This link is saved. Open the original website to read the article.") + "</p>";
        var baseUri = HtmlBase(document, page.Uri);
        var image = Absolute(baseUri, Meta("og:image", "twitter:image"));
        if (image.Length == 0) image = content?.Descendants("img").Select(i => Absolute(baseUri, i.GetAttributeValue("src", ""))).FirstOrDefault(i => i.Length > 0) ?? "";
        return new()
        {
            Id = LocalLibrary.Id("link", url), Title = ArticleText.PlainText(title), Url = url,
            FeedId = LocalLibrary.SavedLinksId, FeedTitle = WebUtility.HtmlDecode(Meta("og:site_name")),
            Html = FeedParser.AbsolutizeHtml(html, baseUri), ImageUrl = image, Author = WebUtility.HtmlDecode(author), Published = published, IsSaved = true
        };
    }

    private static Uri HtmlBase(HtmlDocument document, Uri uri) =>
        Uri.TryCreate(uri, document.DocumentNode.Descendants("base").FirstOrDefault()?.GetAttributeValue("href", ""), out var result) && ArticleText.TryWebUri(result.AbsoluteUri, out _) ? result : uri;

    private static string Absolute(Uri baseUri, string value) => string.IsNullOrWhiteSpace(value) ? "" :
        Uri.TryCreate(baseUri, WebUtility.HtmlDecode(value), out var uri) && ArticleText.TryWebUri(uri.AbsoluteUri, out _) ? uri.AbsoluteUri : "";
}
