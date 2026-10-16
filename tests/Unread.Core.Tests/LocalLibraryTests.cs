using System.Net;
using System.Text;
using System.Xml;
using Unread.Core;
using Xunit;

namespace Unread.Core.Tests;

public sealed class LocalLibraryTests
{
    private const string Rss = """
        <?xml version="1.0" encoding="utf-8"?>
        <rss version="2.0" xmlns:content="http://purl.org/rss/1.0/modules/content/" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:media="http://search.yahoo.com/mrss/">
          <channel><title>A &amp; B</title><link>https://example.com/</link><description>Stories</description>
            <item><guid isPermaLink="false">one</guid><title>First &amp; best</title><link>/one</link><dc:creator>J Smith</dc:creator>
              <pubDate>Mon, 15 Jun 2026 12:00:00 GMT</pubDate><description>Short preview</description>
              <content:encoded><![CDATA[<p>The full story <a href="/more">continues</a>.</p><img src="images/photo.jpg"><script>bad()</script>]]></content:encoded>
              <media:thumbnail url="/thumb.jpg" />
            </item>
            <item><guid>two</guid><title>Without a date</title><description><![CDATA[<p>Another story.</p>]]></description></item>
          </channel>
        </rss>
        """;

    [Fact]
    public void Opml_preserves_nested_folders_escapes_titles_and_deduplicates_across_folders()
    {
        var imported = Opml.Parse("""
            <opml version="2.0"><body><outline text="Arts &amp; Culture"><outline text="Design">
              <outline type="rss" text="A &amp; B" xmlUrl="https://EXAMPLE.com:443/feed?a=1&amp;b=2" htmlUrl="https://example.com/" />
            </outline></outline><outline text="Favorites"><outline text="Duplicate" xmlUrl="https://example.com/feed?a=1&amp;b=2" /></outline>
            <outline type="rss" xmlUrl="file:///C:/private.xml" /><outline type="rss" text="Missing URL" />
            </body></opml>
            """);
        var feed = Assert.Single(imported.Subscriptions);
        Assert.Equal("A & B", feed.Title); Assert.Equal(2, imported.Skipped);
        Assert.Equal(new[] { "Arts & Culture", "Design" }, feed.Categories[0].Path);
        Assert.Equal("https://example.com/feed?a=1&b=2", feed.FeedUrl);
        var roundTrip = Assert.Single(Opml.Parse(Opml.Export(imported.Subscriptions)).Subscriptions);
        Assert.Equal(feed.Title, roundTrip.Title); Assert.Equal(feed.SiteUrl, roundTrip.SiteUrl);
        Assert.Equal(feed.Categories.Select(c => c.Id), roundTrip.Categories.Select(c => c.Id));
    }

    [Fact]
    public void Importing_duplicates_keeps_existing_names_and_merges_folders()
    {
        var original = LocalLibrary.Subscription("https://example.com/feed", "Custom title");
        var library = new LibrarySnapshot { Subscriptions = [original] };
        Assert.Equal(0, LocalLibrary.AddSubscriptions(library, [LocalLibrary.Subscription(original.FeedUrl, "New title", [LocalLibrary.Folder(["News"])])]));
        Assert.Equal("Custom title", Assert.Single(library.Subscriptions).Title);
        Assert.Equal("News", Assert.Single(library.Subscriptions[0].Categories).Label);
    }

    [Theory]
    [InlineData("<!DOCTYPE opml [<!ENTITY x SYSTEM 'file:///C:/private.txt'>]><opml><body>&x;</body></opml>")]
    [InlineData("<opml><body></opml>")]
    public void Opml_rejects_dtd_and_malformed_xml(string xml) => Assert.Throws<XmlException>(() => Opml.Parse(xml));

    [Fact]
    public void Opml_rejects_unrelated_xml() => Assert.Throws<FormatException>(() => Opml.Parse("<html><body/></html>"));

    [Fact]
    public void Opml_stream_honors_the_declared_character_encoding()
    {
        using var stream = new MemoryStream(Encoding.Latin1.GetBytes("<?xml version='1.0' encoding='iso-8859-1'?><opml><body><outline text='Café' xmlUrl='https://example.com/feed'/></body></opml>"));
        Assert.Equal("Café", Assert.Single(Opml.Parse(stream).Subscriptions).Title);
    }

    [Fact]
    public void Rss_uses_a_permalink_guid_when_the_link_is_missing()
    {
        var feed = FeedParser.Parse("<rss><channel><title>Feed</title><item><guid>https://example.com/story</guid><title>Story</title></item></channel></rss>", LocalLibrary.Subscription("https://example.com/feed"), new("https://example.com/feed"));
        Assert.Equal("https://example.com/story", Assert.Single(feed.Articles).Url);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/private.xml")]
    [InlineData("https://username:password@example.com/feed")]
    [InlineData("example.com")]
    public void Local_urls_require_http_and_no_embedded_credentials(string value) => Assert.Throws<FormatException>(() => LocalLibrary.WebUrl(value));

    [Fact]
    public void Rss_reads_full_content_metadata_relative_links_and_stable_ids()
    {
        var subscription = LocalLibrary.Subscription("https://example.com/feed", "Example");
        var feed = FeedParser.Parse(Rss, subscription, new("https://example.com/feed"));
        Assert.Equal("A & B", feed.Title); Assert.Equal(2, feed.Articles.Count);
        var article = feed.Articles[0];
        Assert.Equal("First & best", article.Title); Assert.Equal("J Smith", article.Author);
        Assert.Equal("https://example.com/one", article.Url); Assert.Equal("https://example.com/thumb.jpg", article.ImageUrl);
        Assert.Contains("The full story", article.Html); Assert.Contains("https://example.com/more", article.Html);
        Assert.Contains("https://example.com/images/photo.jpg", article.Html); Assert.DoesNotContain("bad()", article.Html);
        Assert.Equal(new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero), article.Published);
        Assert.Equal(article.Id, FeedParser.Parse(Rss.Replace("First &amp; best", "Updated headline"), subscription, new(subscription.FeedUrl)).Articles[0].Id);
    }

    [Fact]
    public void Atom_reads_xhtml_xml_base_authors_and_published_dates()
    {
        var feed = FeedParser.Parse("""
            <feed xmlns="http://www.w3.org/2005/Atom" xml:base="https://example.com/blog/">
              <title>Journal</title><author><name>A Writer</name></author><link href="./"/>
              <entry xml:base="2026/"><id>urn:post:one</id><title>A thought</title><published>2026-06-15T12:00:00Z</published>
                <link rel="self" href="one.atom"/><link rel="alternate" type="text/html" href="one"/>
                <content type="xhtml"><div xmlns="http://www.w3.org/1999/xhtml"><p>Readable <em>text</em>.</p><img src="photo.jpg"/></div></content>
              </entry>
            </feed>
            """, LocalLibrary.Subscription("https://example.com/atom"), new("https://example.com/atom"));
        var article = Assert.Single(feed.Articles);
        Assert.Equal("https://example.com/blog/2026/one", article.Url); Assert.Equal("A Writer", article.Author);
        Assert.Contains("Readable", article.Html); Assert.Contains("<em>text</em>", article.Html);
        Assert.Equal("https://example.com/blog/2026/photo.jpg", article.ImageUrl);
    }

    [Fact]
    public void Rss_one_rdf_feeds_are_supported()
    {
        var feed = FeedParser.Parse("""
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns="http://purl.org/rss/1.0/">
              <channel><title>RDF feed</title><link>https://example.com/</link></channel>
              <item rdf:about="https://example.com/one"><title>One</title><link>https://example.com/one</link><description>Readable</description></item>
            </rdf:RDF>
            """, LocalLibrary.Subscription("https://example.com/rdf"), new("https://example.com/rdf"));
        Assert.Equal("One", Assert.Single(feed.Articles).Title);
    }

    [Fact]
    public void Refresh_keeps_read_and_saved_flags_and_undated_article_order()
    {
        var subscription = LocalLibrary.Subscription("https://example.com/feed");
        var library = new LibrarySnapshot();
        LocalLibrary.MergeArticles(library, FeedParser.Parse(Rss, subscription, new(subscription.FeedUrl)).Articles);
        var existing = library.Articles.Single(a => a.Title == "Without a date");
        existing.IsUnread = false; existing.IsSaved = true;
        var date = existing.Published;
        LocalLibrary.MergeArticles(library, FeedParser.Parse(Rss, subscription, new(subscription.FeedUrl)).Articles);
        Assert.Equal(2, library.Articles.Count);
        var refreshed = library.Articles.Single(a => a.Id == existing.Id);
        Assert.False(refreshed.IsUnread); Assert.True(refreshed.IsSaved); Assert.Equal(date, refreshed.Published);
    }

    [Fact]
    public async Task Website_discovery_resolves_relative_urls_and_offers_multiple_feeds()
    {
        var handler = new StubHandler(_ => new(HttpStatusCode.OK)
        {
            RequestMessage = new(HttpMethod.Get, "https://example.com/blog/"),
            Content = new StringContent("""
                <html><head><base href="/feeds/"><link rel="alternate" type="application/rss+xml" title="Posts" href="posts.xml">
                <link rel="alternate" type="application/atom+xml" title="Comments" href="comments.atom">
                <link rel="alternate" type="application/rss+xml" href="posts.xml"><link rel="alternate" type="text/html" href="/other"></head></html>
                """)
        });
        var discovery = await new LocalWebClient(new(handler)).DiscoverAsync("https://example.com/old");
        Assert.Null(discovery.Feed); Assert.Equal(2, discovery.Feeds.Count);
        Assert.Equal("https://example.com/feeds/posts.xml", discovery.Feeds[0].Url);
        Assert.All(handler.Requests, r => Assert.Null(r.Authorization));
    }

    [Fact]
    public async Task Direct_feeds_are_detected_without_duplicate_downloads()
    {
        var handler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(Rss) });
        var discovery = await new LocalWebClient(new(handler)).DiscoverAsync("https://example.com/feed");
        Assert.Single(discovery.Feeds); Assert.NotNull(discovery.Feed); Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Saved_articles_extract_metadata_and_content_without_navigation_or_scripts()
    {
        var handler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("""
            <html><head><title>Fallback</title><meta property="og:title" content="A &amp; B"><meta name="author" content="Author">
            <meta property="og:site_name" content="A Journal"><meta property="og:image" content="/cover.jpg"></head>
            <body><nav>Outside navigation</nav><article><header>Header junk</header><h1>A &amp; B</h1><p>Useful content.</p>
              <p><a href="/next">Next</a></p><script>bad()</script><footer>Footer junk</footer></article></body></html>
            """) });
        var article = await new LocalWebClient(new(handler)).FetchArticleAsync("https://example.com/story#section");
        Assert.True(article.IsSaved); Assert.True(article.IsUnread); Assert.Equal(LocalLibrary.SavedLinksId, article.FeedId);
        Assert.Equal("A & B", article.Title); Assert.Equal("A Journal", article.FeedTitle); Assert.Equal("Author", article.Author);
        Assert.Equal("https://example.com/story", article.Url); Assert.Equal("https://example.com/cover.jpg", article.ImageUrl);
        Assert.Contains("Useful content", article.Html); Assert.Contains("https://example.com/next", article.Html);
        Assert.DoesNotContain("junk", article.Html); Assert.DoesNotContain("bad()", article.Html);
    }

    [Fact]
    public async Task Download_limits_and_http_errors_do_not_replace_the_library()
    {
        var handler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[8 * 1024 * 1024 + 1]) });
        await Assert.ThrowsAsync<FormatException>(() => new LocalWebClient(new(handler)).DiscoverAsync("https://example.com/feed"));
        var missing = new LocalWebClient(new(new StubHandler(_ => new(HttpStatusCode.NotFound))));
        await Assert.ThrowsAsync<HttpRequestException>(() => missing.FetchFeedAsync(LocalLibrary.Subscription("https://example.com/missing")));
    }

    [Fact]
    public async Task A_stalled_response_body_is_cancelled_after_the_download_deadline()
    {
        var handler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) });
        var client = new LocalWebClient(new(handler), TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DiscoverAsync("https://example.com/feed"));
    }

    [Fact]
    public async Task Local_library_persists_subscriptions_folders_saved_links_and_read_state()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Unread-local-test-" + Guid.NewGuid());
        try
        {
            var store = new LibraryStore(directory);
            var feed = LocalLibrary.Subscription("https://example.com/feed", "Example", [LocalLibrary.Folder(["News", "Tech"])]);
            var library = new LibrarySnapshot { Subscriptions = [feed], Articles = [new() { Id = "link", FeedId = LocalLibrary.SavedLinksId, IsSaved = true, IsUnread = false }] };
            await store.SaveAsync(library);
            var loaded = (await store.LoadAsync())!;
            Assert.Equal(feed.FeedUrl, Assert.Single(loaded.Subscriptions).FeedUrl);
            Assert.Equal(feed.Categories[0].Path, loaded.Subscriptions[0].Categories[0].Path);
            Assert.True(Assert.Single(loaded.Articles).IsSaved); Assert.False(loaded.Articles[0].IsUnread); Assert.Empty(loaded.Pending);
        }
        finally { Directory.Delete(directory, true); }
    }
}

internal sealed class StalledStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    { await Task.Delay(Timeout.Infinite, ct); return 0; }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
