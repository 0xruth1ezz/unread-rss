using HtmlAgilityPack;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Net;
using Unread.Core;

namespace Unread.App;

/// <summary>Feed HTML becomes native selectable XAML. No browser, scripts or embedded frames.</summary>
internal static class NativeArticleRenderer
{
    public static void Render(StackPanel target, Article article, double fontSize, bool demo)
    {
        target.Children.Clear();
        var document = ArticleText.Parse(article.Html);
        AddChildren(target, document.DocumentNode, article.Url, fontSize, demo);
        if (target.Children.Count == 0)
            target.Children.Add(Text("This feed provides a link to the article. Open the original website to keep reading.", fontSize));
    }

    private static void AddBlock(StackPanel panel, HtmlNode node, string baseUrl, double size, bool demo)
    {
        if (node.NodeType == HtmlNodeType.Comment) return;
        if (node.NodeType == HtmlNodeType.Text)
        {
            var text = WebUtility.HtmlDecode(node.InnerText).Trim();
            if (!string.IsNullOrWhiteSpace(text)) panel.Children.Add(Text(text, size));
            return;
        }
        if (node.Name == "img")
        {
            var source = Resolve(node.GetAttributeValue("src", ""), baseUrl);
            if (source is null || !(source.Scheme is "https" or "http" || (demo && source.Scheme == "ms-appx"))) return;
            var image = new Image { Source = new BitmapImage(source), Stretch = Stretch.Uniform, MaxHeight = 560, HorizontalAlignment = HorizontalAlignment.Stretch };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(image, node.GetAttributeValue("alt", "Article image"));
            var anchor = node.Ancestors("a").FirstOrDefault();
            var link = anchor is null ? null : Resolve(anchor.GetAttributeValue("href", ""), baseUrl);
            if (link is not null && ArticleText.TryWebUri(link.AbsoluteUri, out _))
                panel.Children.Add(new HyperlinkButton { Content = image, NavigateUri = link, Padding = new(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
            else panel.Children.Add(image);
            return;
        }
        if (node.Name is "p" or "h1" or "h2" or "h3" or "h4" or "blockquote" or "pre" or "li" or "figcaption")
        {
            var heading = node.Name.StartsWith('h') && node.Name.Length == 2;
            var block = Text("", heading ? size + 7 : node.Name == "figcaption" ? size - 4 : size);
            if (heading) { block.FontFamily = (FontFamily)Application.Current.Resources["HeadlineFont"]; block.Margin = new(0, 12, 0, 0); }
            if (node.Name == "blockquote") { block.FontStyle = Windows.UI.Text.FontStyle.Italic; block.Margin = new(18, 4, 8, 4); }
            var paragraph = new Paragraph();
            if (node.Name == "li") paragraph.Inlines.Add(new Run { Text = "•  " });
            foreach (var child in node.ChildNodes) AddInline(paragraph.Inlines, child, baseUrl);
            block.Blocks.Clear();
            block.Blocks.Add(paragraph);
            if (!string.IsNullOrWhiteSpace(node.InnerText)) panel.Children.Add(block);
            foreach (var image in node.Descendants("img")) AddBlock(panel, image, baseUrl, size, demo);
            return;
        }
        if (node.Name == "hr")
        { panel.Children.Add(new Border { Height = 1, Background = Palette.Brush("ReaderDivider"), Margin = new(0, 12, 0, 12) }); return; }
        AddChildren(panel, node, baseUrl, size, demo);
    }

    private static void AddChildren(StackPanel panel, HtmlNode container, string baseUrl, double size, bool demo)
    {
        // Group loose inline siblings so <div>Hello <b>world</b>!</div> stays one paragraph.
        var inlineGroup = new List<HtmlNode>();
        void Flush()
        {
            if (inlineGroup.Any(n => !string.IsNullOrWhiteSpace(n.InnerText)))
            {
                var block = Text("", size);
                var paragraph = new Paragraph();
                foreach (var child in inlineGroup) AddInline(paragraph.Inlines, child, baseUrl);
                block.Blocks.Clear(); block.Blocks.Add(paragraph); panel.Children.Add(block);
            }
            inlineGroup.Clear();
        }
        foreach (var child in container.ChildNodes)
        {
            if (ArticleText.CanGroupInline(child)) inlineGroup.Add(child);
            else { Flush(); AddBlock(panel, child, baseUrl, size, demo); }
        }
        Flush();
    }

    private static RichTextBlock Text(string text, double size)
    {
        var block = new RichTextBlock
        {
            FontFamily = (FontFamily)Application.Current.Resources["ReadingFont"], FontSize = size,
            LineHeight = size * 1.55, Foreground = Palette.Brush("ReaderForeground"),
            IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap
        };
        var paragraph = new Paragraph();
        paragraph.Inlines.Add(new Run { Text = text });
        block.Blocks.Add(paragraph);
        return block;
    }

    private static void AddInline(InlineCollection target, HtmlNode node, string baseUrl)
    {
        if (node.NodeType == HtmlNodeType.Text) { target.Add(new Run { Text = WebUtility.HtmlDecode(node.InnerText) }); return; }
        if (node.Name == "br") { target.Add(new LineBreak()); return; }
        if (node.Name == "img" || node.NodeType == HtmlNodeType.Comment) return;
        Span span = node.Name switch { "strong" or "b" => new Bold(), "em" or "i" => new Italic(), "u" => new Underline(), _ => new Span() };
        if (node.Name == "a" && Resolve(node.GetAttributeValue("href", ""), baseUrl) is { } uri && ArticleText.TryWebUri(uri.AbsoluteUri, out _))
        {
            var link = new Hyperlink { Foreground = Palette.Brush("ReaderAccent"), NavigateUri = uri };
            span = link;
        }
        foreach (var child in node.ChildNodes) AddInline(span.Inlines, child, baseUrl);
        target.Add(span);
    }

    private static Uri? Resolve(string value, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute)) return absolute;
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var basis) && Uri.TryCreate(basis, value, out var relative) ? relative : null;
    }
}
