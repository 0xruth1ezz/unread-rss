using HtmlAgilityPack;
using System.Net;
using System.Text.RegularExpressions;

namespace Unread.Core;

public static partial class ArticleText
{
    public static HtmlDocument Parse(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        foreach (var node in document.DocumentNode.SelectNodes("//script|//style|//iframe|//object|//embed|//form|//svg|//noscript") ?? Enumerable.Empty<HtmlNode>())
            node.Remove();
        return document;
    }

    public static string PlainText(string html) => Whitespace().Replace(
        WebUtility.HtmlDecode(Parse(html).DocumentNode.InnerText), " ").Trim();

    public static bool TryWebUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var result) &&
            (result.Scheme == Uri.UriSchemeHttps || result.Scheme == Uri.UriSchemeHttp))
        { uri = result; return true; }
        uri = null!;
        return false;
    }

    // Images are block content in the native reader, even inside an inline link or span.
    public static bool CanGroupInline(HtmlNode node) =>
        node.Name != "img" && !node.Descendants("img").Any() &&
        (node.NodeType == HtmlNodeType.Text || node.Name is "a" or "b" or "strong" or "em" or "i" or "span" or "br" or "u" or "code");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
