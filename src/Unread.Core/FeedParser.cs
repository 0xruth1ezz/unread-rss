using System.Globalization;
using System.Net;
using System.Xml.Linq;

namespace Unread.Core;

public sealed record ParsedFeed(string Title, string SiteUrl, List<Article> Articles);

public static class FeedParser
{
    public static ParsedFeed Parse(string xml, Subscription subscription, Uri documentUri)
    {
        var document = Opml.ReadXml(xml);
        var root = document.Root ?? throw new FormatException("The feed is empty.");
        var atom = root.Name == XName.Get("feed", "http://www.w3.org/2005/Atom");
        var channel = root.Name.LocalName == "rss" ? root.Element("channel") : root.Name.LocalName == "RDF" ? root.Elements().FirstOrDefault(e => e.Name.LocalName == "channel") : root;
        if ((!atom && root.Name.LocalName is not ("rss" or "RDF")) || channel is null)
            throw new FormatException("This address does not contain an RSS or Atom feed.");
        static XElement? Child(XElement element, string name) => element.Elements().FirstOrDefault(e => e.Name.LocalName == name);
        static string Value(XElement element, string name) => Child(element, name)?.Value.Trim() ?? "";
        string Link(XElement element)
        {
            var link = atom ? element.Elements(root.Name.Namespace + "link").FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate" && (string?)l.Attribute("type") is null or "text/html" or "application/xhtml+xml") : Child(element, "link");
            return Resolve(link, atom ? (string?)link?.Attribute("href") : link?.Value, documentUri);
        }
        var title = WebUtility.HtmlDecode(Value(channel, "title"));
        var site = Link(channel);
        var items = atom ? root.Elements(root.Name.Namespace + "entry") : root.Name.LocalName == "RDF" ? root.Elements().Where(e => e.Name.LocalName == "item") : channel.Elements("item");
        var articles = new List<Article>();
        foreach (var item in items.Take(1000))
        {
            var url = Link(item);
            if (!atom && url.Length == 0 && Child(item, "guid") is { } guid && (string?)guid.Attribute("isPermaLink") != "false")
                url = Resolve(guid, guid.Value, documentUri);
            var identity = Value(item, atom ? "id" : "guid");
            if (identity.Length == 0) identity = url;
            var headline = WebUtility.HtmlDecode(Value(item, "title"));
            var date = Value(item, atom ? "published" : "pubDate");
            if (date.Length == 0) date = Value(item, atom ? "updated" : "date");
            var published = DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed) ? parsed : default;
            if (identity.Length == 0) identity = headline + "\n" + date;
            if (string.IsNullOrWhiteSpace(identity)) continue;
            var content = atom ? Child(item, "content") ?? Child(item, "summary") : item.Elements().FirstOrDefault(e => e.Name.LocalName == "encoded") ?? Child(item, "description");
            var html = content?.Value ?? "";
            if (atom && (string?)content?.Attribute("type") == "xhtml") html = string.Concat(content.Elements().Select(e => e.ToString(SaveOptions.DisableFormatting)));
            else if (atom && (string?)content?.Attribute("type") is not ("html" or "xhtml")) html = "<p>" + WebUtility.HtmlEncode(html) + "</p>";
            var contentBase = BaseUri(content ?? item, documentUri);
            html = AbsolutizeHtml(html, contentBase);
            var author = atom ? Value(Child(item, "author") ?? Child(channel, "author") ?? item, "name") : Value(item, "creator");
            if (author.Length == 0 && !atom) author = Value(item, "author");
            var imageElement = item.Descendants().FirstOrDefault(e => (e.Name.LocalName == "thumbnail" || e.Name.LocalName == "content" && ((string?)e.Attribute("medium") == "image" || ((string?)e.Attribute("type"))?.StartsWith("image/") == true)) && e.Attribute("url") is not null)
                ?? item.Elements().FirstOrDefault(e => e.Name.LocalName == "enclosure" && ((string?)e.Attribute("type"))?.StartsWith("image/") == true);
            var image = Resolve(imageElement, (string?)imageElement?.Attribute("url"), documentUri);
            if (image.Length == 0) image = ArticleText.Parse(html).DocumentNode.Descendants("img").Select(i => i.GetAttributeValue("src", "")).FirstOrDefault(s => ArticleText.TryWebUri(s, out _)) ?? "";
            articles.Add(new()
            {
                Id = LocalLibrary.Id("article", subscription.Id + "\n" + identity), FeedId = subscription.Id,
                FeedTitle = subscription.Title, Title = string.IsNullOrWhiteSpace(headline) ? "Untitled article" : ArticleText.PlainText(headline),
                Url = url.Length > 0 ? url : site, Html = html, ImageUrl = image, Published = published, Author = WebUtility.HtmlDecode(author)
            });
        }
        return new(title, site, articles.DistinctBy(a => a.Id).ToList());
    }

    private static Uri BaseUri(XElement element, Uri documentUri)
    {
        var result = documentUri;
        foreach (var ancestor in element.AncestorsAndSelf().Reverse())
            if (Uri.TryCreate(result, (string?)ancestor.Attribute(XNamespace.Xml + "base"), out var next)) result = next;
        return result;
    }

    private static string Resolve(XElement? element, string? value, Uri documentUri) =>
        string.IsNullOrWhiteSpace(value) ? "" : Uri.TryCreate(element is null ? documentUri : BaseUri(element, documentUri), value.Trim(), out var uri) && ArticleText.TryWebUri(uri.AbsoluteUri, out _) ? uri.AbsoluteUri : "";

    internal static string AbsolutizeHtml(string html, Uri baseUri)
    {
        var document = ArticleText.Parse(html);
        foreach (var node in document.DocumentNode.Descendants())
        {
            foreach (var name in new[] { "href", "src", "poster" })
            {
                var value = node.GetAttributeValue(name, "");
                if (value.Length == 0) continue;
                if (Uri.TryCreate(baseUri, WebUtility.HtmlDecode(value), out var absolute) && ArticleText.TryWebUri(absolute.AbsoluteUri, out _)) node.SetAttributeValue(name, absolute.AbsoluteUri);
                else node.Attributes.Remove(name);
            }
        }
        return document.DocumentNode.InnerHtml;
    }
}
