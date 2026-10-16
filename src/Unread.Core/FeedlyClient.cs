using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Unread.Core;

public sealed class FeedlyException(string message, HttpStatusCode status) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

public sealed class FeedlyClient
{
    private readonly HttpClient _http;
    private readonly Func<string?> _token;

    public FeedlyClient(HttpClient http, Func<string?> token)
    {
        _http = http;
        _http.BaseAddress ??= new Uri("https://cloud.feedly.com/v3/");
        _token = token;
    }

    public async Task<FeedProfile> GetProfileAsync(CancellationToken ct = default)
    {
        using var doc = await GetAsync("profile", ct);
        var root = doc.RootElement;
        return new(Text(root, "id"), Text(root, "fullName", Text(root, "email", "Feedly")));
    }

    public async Task<List<Subscription>> GetSubscriptionsAsync(CancellationToken ct = default)
    {
        using var doc = await GetAsync("subscriptions", ct);
        return doc.RootElement.EnumerateArray().Select(item => new Subscription(
            Text(item, "id"), Text(item, "title", "Untitled feed"),
            item.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array
                ? cats.EnumerateArray().Select(c => new FeedCategory(Text(c, "id"), Text(c, "label"))).ToList() : [])).ToList();
    }

    public async Task<Dictionary<string, int>> GetUnreadCountsAsync(CancellationToken ct = default)
    {
        using var doc = await GetAsync("markers/counts", ct);
        return doc.RootElement.TryGetProperty("unreadcounts", out var counts)
            ? counts.EnumerateArray().ToDictionary(x => Text(x, "id"), x => x.GetProperty("count").GetInt32()) : [];
    }

    public async Task<StreamPage> GetStreamAsync(string streamId, bool unreadOnly, string? continuation = null, CancellationToken ct = default)
    {
        var path = $"streams/contents?streamId={Uri.EscapeDataString(streamId)}&count=50&ranked=newest&unreadOnly={unreadOnly.ToString().ToLowerInvariant()}";
        if (!string.IsNullOrEmpty(continuation)) path += "&continuation=" + Uri.EscapeDataString(continuation);
        using var doc = await GetAsync(path, ct);
        var root = doc.RootElement;
        return new(root.TryGetProperty("items", out var items) ? items.EnumerateArray().Select(ParseArticle).ToList() : [],
            root.TryGetProperty("continuation", out var next) ? next.GetString() : null);
    }

    public async Task ApplyAsync(string userId, PendingAction action, CancellationToken ct = default)
    {
        if (action.Kind == "read")
        {
            await SendAsync(HttpMethod.Post, "markers", new
            {
                action = action.Value ? "markAsRead" : "keepUnread", type = "entries", entryIds = new[] { action.ArticleId }
            }, ct);
        }
        else if (action.Kind == "saved")
        {
            var tag = $"user/{userId}/tag/global.saved";
            if (action.Value)
                await SendAsync(HttpMethod.Put, "tags/" + Uri.EscapeDataString(tag), new { entryId = action.ArticleId }, ct);
            else
                await SendAsync(HttpMethod.Delete, "tags/" + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(action.ArticleId), null, ct);
        }
        else throw new ArgumentException("Unknown article action.", nameof(action));
    }

    private static Article ParseArticle(JsonElement item)
    {
        var origin = item.TryGetProperty("origin", out var o) ? o : default;
        var html = item.TryGetProperty("content", out var content) ? Text(content, "content") : "";
        if (string.IsNullOrWhiteSpace(html) && item.TryGetProperty("summary", out var summary)) html = Text(summary, "content");
        var url = "";
        if (item.TryGetProperty("canonicalUrl", out var canonical)) url = canonical.GetString() ?? "";
        if (string.IsNullOrEmpty(url) && item.TryGetProperty("alternate", out var links) && links.GetArrayLength() > 0)
            url = Text(links[0], "href");
        var image = item.TryGetProperty("visual", out var visual) ? Text(visual, "url") : "";
        if (!ArticleText.TryWebUri(image, out _))
            image = ArticleText.Parse(html).DocumentNode.SelectSingleNode("//img")?.GetAttributeValue("src", "") ?? "";
        return new()
        {
            Id = Text(item, "id"), Title = WebUtility.HtmlDecode(Text(item, "title", "Untitled article")),
            FeedId = Text(origin, "streamId"), FeedTitle = Text(origin, "title", "Feedly"),
            Author = Text(item, "author"), Url = url, Html = html, ImageUrl = image,
            Published = item.TryGetProperty("published", out var time) && time.TryGetInt64(out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : DateTimeOffset.UtcNow,
            IsUnread = !item.TryGetProperty("unread", out var unread) || unread.GetBoolean(),
            IsSaved = item.TryGetProperty("tags", out var tags) && tags.EnumerateArray().Any(t => Text(t, "id").EndsWith("/tag/global.saved", StringComparison.Ordinal))
        };
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, path);
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private async Task SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = Request(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var token = _token();
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Connect your Feedly account first.");
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new("application/json"));
        return request;
    }

    private static Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken _)
    {
        if (response.IsSuccessStatusCode) return Task.CompletedTask;
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Your Feedly token has expired or is invalid. Reconnect in Account settings.",
            HttpStatusCode.Forbidden => "This Feedly account does not have access to this API. Check your token's permissions.",
            HttpStatusCode.TooManyRequests => "Feedly's request limit was reached. Your changes are safe; try syncing later.",
            _ => $"Feedly could not complete the request ({(int)response.StatusCode}). Try syncing again."
        };
        throw new FeedlyException(message, response.StatusCode);
    }

    private static string Text(JsonElement element, string name, string fallback = "") =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback : fallback;
}
