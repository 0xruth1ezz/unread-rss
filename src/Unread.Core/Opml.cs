using System.Xml;
using System.Xml.Linq;

namespace Unread.Core;

public sealed record OpmlImport(List<Subscription> Subscriptions, int Skipped)
{
    public List<FeedCategory> Folders { get; init; } = [];
    public Dictionary<string, List<string>> SidebarOrder { get; init; } = [];
}

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
        var subscriptions = new List<Subscription>();
        var skipped = 0;
        void Remember(string parent, string id)
        {
            if (!feeds.SidebarOrder.TryGetValue(parent, out var order)) feeds.SidebarOrder[parent] = order = [];
            if (!order.Contains(id)) order.Add(id);
        }
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
                        var subscription = LocalLibrary.Subscription(url, title, path.Length == 0 ? [] : [LocalLibrary.Folder(path)], site);
                        subscriptions.Add(subscription);
                        Remember(path.Length == 0 ? "" : LocalLibrary.Folder(path).Id, subscription.Id);
                    }
                    catch (FormatException) { skipped++; }
                }
                else if (Attribute("type").Equals("rss", StringComparison.OrdinalIgnoreCase)) skipped++;
                else
                {
                    if (string.IsNullOrWhiteSpace(title)) { Visit(outline, path); continue; }
                    var folderPath = path.Append(title).ToArray();
                    var folder = feeds.Folders.FirstOrDefault(f => f.Path.SequenceEqual(folderPath, StringComparer.OrdinalIgnoreCase));
                    if (folder is null) { folder = LocalLibrary.Folder(folderPath); feeds.Folders.Add(folder); }
                    Remember(path.Length == 0 ? "" : LocalLibrary.Folder(path).Id, folder.Id);
                    Visit(outline, folder.Path);
                }
            }
        }
        Visit(body, []);
        LocalLibrary.AddSubscriptions(feeds, subscriptions);
        return new(feeds.Subscriptions, skipped) { Folders = feeds.Folders, SidebarOrder = feeds.SidebarOrder };
    }

    public static string Export(IEnumerable<Subscription> subscriptions) => Export(new LibrarySnapshot { Subscriptions = subscriptions.ToList() });

    public static string Export(LibrarySnapshot library)
    {
        library = SidebarOrganization.CopyForEdit(library);
        SidebarOrganization.Normalize(library);
        var body = new XElement("body");
        var subscriptions = library.Subscriptions.ToDictionary(f => f.Id);
        void WriteChildren(XElement parent, string parentId)
        {
            foreach (var entry in SidebarOrganization.Children(library, parentId))
            {
                if (entry.IsFolder)
                {
                    var folder = new XElement("outline", new XAttribute("text", entry.Title), new XAttribute("title", entry.Title));
                    parent.Add(folder); WriteChildren(folder, entry.Id); continue;
                }
                var feed = subscriptions[entry.Id];
                if (string.IsNullOrEmpty(feed.FeedUrl)) continue;
                var outline = new XElement("outline", new XAttribute("type", "rss"), new XAttribute("text", feed.Title), new XAttribute("title", feed.Title), new XAttribute("xmlUrl", feed.FeedUrl));
                if (!string.IsNullOrEmpty(feed.SiteUrl)) outline.Add(new XAttribute("htmlUrl", feed.SiteUrl));
                parent.Add(outline);
            }
        }
        WriteChildren(body, "");
        return new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("opml", new XAttribute("version", "2.0"),
            new XElement("head", new XElement("title", "Unread subscriptions")), body)).ToString();
    }
}
