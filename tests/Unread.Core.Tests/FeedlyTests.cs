using System.Net;
using System.Text;
using System.Text.Json;
using Unread.Core;
using Xunit;

namespace Unread.Core.Tests;

public sealed class FeedlyTests
{
    [Fact]
    public async Task Streams_encode_ids_and_continuations_and_preserve_content()
    {
        var handler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("""
            {"continuation":"next/page+2","items":[{"id":"entry/1","title":"A &amp; B","published":1700000000000,"unread":false,
              "origin":{"streamId":"feed/https://example.com/rss","title":"Example"},"content":{"content":"<p>Full story</p>"},
              "alternate":[{"href":"https://example.com/story"}],"tags":[{"id":"user/abc/tag/global.saved"}]}]}
            """) });
        var client = new FeedlyClient(new(handler), () => "test-token");
        var page = await client.GetStreamAsync("feed/https://example.com/rss?a=b&c=d", true, "next/a+b");
        Assert.Contains("streamId=feed%2Fhttps%3A%2F%2Fexample.com%2Frss%3Fa%3Db%26c%3Dd", handler.Requests[0].Uri);
        Assert.Contains("continuation=next%2Fa%2Bb", handler.Requests[0].Uri);
        Assert.Contains("unreadOnly=true", handler.Requests[0].Uri);
        Assert.Equal("Bearer test-token", handler.Requests[0].Authorization);
        Assert.Equal("next/page+2", page.Continuation);
        var article = Assert.Single(page.Items);
        Assert.Equal("A & B", article.Title); Assert.False(article.IsUnread); Assert.True(article.IsSaved);
        Assert.Equal("<p>Full story</p>", article.Html); Assert.Equal("https://example.com/story", article.Url);
    }

    [Fact]
    public async Task Read_and_save_actions_use_the_correct_endpoints_and_verbs()
    {
        var handler = new StubHandler(_ => new(HttpStatusCode.OK));
        var client = new FeedlyClient(new(handler), () => "test");
        await client.ApplyAsync("person", new("entry/1", "read", true));
        await client.ApplyAsync("person", new("entry/1", "read", false));
        await client.ApplyAsync("person", new("entry/1", "saved", true));
        await client.ApplyAsync("person", new("entry/1", "saved", false));
        Assert.Equal("markAsRead", JsonDocument.Parse(handler.Requests[0].Body!).RootElement.GetProperty("action").GetString());
        Assert.Equal("keepUnread", JsonDocument.Parse(handler.Requests[1].Body!).RootElement.GetProperty("action").GetString());
        Assert.Equal("PUT", handler.Requests[2].Method);
        Assert.EndsWith("tags/user%2Fperson%2Ftag%2Fglobal.saved", handler.Requests[2].Uri);
        Assert.Equal("entry/1", JsonDocument.Parse(handler.Requests[2].Body!).RootElement.GetProperty("entryId").GetString());
        Assert.Equal("DELETE", handler.Requests[3].Method);
        Assert.EndsWith("tags/user%2Fperson%2Ftag%2Fglobal.saved/entry%2F1", handler.Requests[3].Uri);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "expired")]
    [InlineData(HttpStatusCode.Forbidden, "permissions")]
    [InlineData(HttpStatusCode.TooManyRequests, "limit")]
    public async Task Failures_offer_actionable_messages_without_exposing_tokens(HttpStatusCode status, string expected)
    {
        var client = new FeedlyClient(new(new StubHandler(_ => new(status))), () => "sensitive-token");
        var error = await Assert.ThrowsAsync<FeedlyException>(() => client.GetProfileAsync());
        Assert.Contains(expected, error.Message); Assert.DoesNotContain("sensitive-token", error.Message);
    }

    [Fact]
    public void Reader_content_excludes_executable_elements_and_unsafe_urls()
    {
        Assert.Equal("Hello & goodbye", ArticleText.PlainText("<script>secret()</script><style>hide</style><p>Hello &amp; goodbye</p>"));
        Assert.False(ArticleText.TryWebUri("javascript:alert(1)", out _));
        Assert.False(ArticleText.TryWebUri("file:///C:/private.txt", out _));
        Assert.True(ArticleText.TryWebUri("https://example.com", out _));
    }

    [Theory]
    [InlineData("<a href='https://example.com'><img src='https://example.com/photo.jpg'></a>")]
    [InlineData("<span><a href='https://example.com'><img src='https://example.com/photo.jpg'></a></span>")]
    public void Image_only_links_are_preserved_as_native_block_content(string html)
    {
        var wrapper = Assert.Single(ArticleText.Parse("<div>" + html + "</div>").DocumentNode.FirstChild.ChildNodes);
        Assert.False(ArticleText.CanGroupInline(wrapper));
        Assert.Single(wrapper.Descendants("img"));
        Assert.True(ArticleText.CanGroupInline(ArticleText.Parse("<a href='https://example.com'>A text link</a>").DocumentNode.FirstChild));
    }
}

internal sealed record RequestRecord(string Method, string Uri, string? Authorization, string? Body);
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    public List<RequestRecord> Requests { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(new(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
        return response(request);
    }
}
