using System.Xml;
using System.Xml.Linq;

namespace Unread.Core;

public sealed record OpmlImport(List<Subscription> Subscriptions, int Skipped);

public static class Opml
{
    internal static XDocument ReadXml(string xml)
    {
        using var input = new StringReader(xml);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 10 * 1024 * 1024 });
        return XDocument.Load(reader);
    }

    public static OpmlImport Parse(string xml) => Parse(ReadXml(xml));

    public static OpmlImport Parse(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 10 * 1024 * 1024 });
        return Parse(XDocument.Load(reader));
    }

    private static OpmlImport Parse(XDocument document)
    {
        if (document.Root?.Name.LocalName != "opml") throw new FormatException("This file is not an OPML subscription list.");
        var body = document.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "body")
            ?? throw new FormatException("The OPML file has no subscription list.");
        var feeds = new LibrarySnapshot();
        var skipped = 0;
        void Visit(XElement parent, string[] path)
        {
            if (path.Length > 64) throw new FormatException("This OPML file contains more than 64 nested folders.");
            foreach (var outline in parent.Elements().Where(e => e.Name.LocalName == "outline"))
            {
                string Attribute(string name) => outline.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value ?? "";
                var url = Attribute("xmlUrl");
                var title = Attribute("title");
                if (string.IsNullOrWhiteSpace(title)) title = Attribute("text");
                if (!string.IsNullOrWhiteSpace(url))
                {
                    try
                    {
                        var site = ArticleText.TryWebUri(Attribute("htmlUrl"), out var siteUri) ? siteUri.AbsoluteUri : "";
                        LocalLibrary.AddSubscriptions(feeds, [LocalLibrary.Subscription(url, title, path.Length == 0 ? [] : [LocalLibrary.Folder(path)], site)]);
                    }
                    catch (FormatException) { skipped++; }
                }
                else if (Attribute("type").Equals("rss", StringComparison.OrdinalIgnoreCase)) skipped++;
                else Visit(outline, string.IsNullOrWhiteSpace(title) ? path : [.. path, title]);
            }
        }
        Visit(body, []);
        return new(feeds.Subscriptions, skipped);
    }

    public static string Export(IEnumerable<Subscription> subscriptions)
    {
        var body = new XElement("body");
        foreach (var feed in subscriptions.Where(s => !string.IsNullOrEmpty(s.FeedUrl)))
        {
            foreach (var category in feed.Categories.Count == 0 ? new FeedCategory?[] { null } : feed.Categories.Cast<FeedCategory?>())
            {
                var parent = body;
                foreach (var folder in category is null ? [] : category.Path.Length > 0 ? category.Path : new[] { category.Label })
                {
                    var node = parent.Elements("outline").FirstOrDefault(e => e.Attribute("xmlUrl") is null && (string?)e.Attribute("text") == folder);
                    if (node is null) { node = new XElement("outline", new XAttribute("text", folder), new XAttribute("title", folder)); parent.Add(node); }
                    parent = node;
                }
                var outline = new XElement("outline", new XAttribute("type", "rss"), new XAttribute("text", feed.Title), new XAttribute("title", feed.Title), new XAttribute("xmlUrl", feed.FeedUrl));
                if (!string.IsNullOrEmpty(feed.SiteUrl)) outline.Add(new XAttribute("htmlUrl", feed.SiteUrl));
                parent.Add(outline);
            }
        }
        return new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("opml", new XAttribute("version", "2.0"),
            new XElement("head", new XElement("title", "Unread subscriptions")), body)).ToString();
    }
}
