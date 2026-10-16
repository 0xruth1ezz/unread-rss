using Unread.Core;

namespace Unread.App;

internal static class DemoLibrary
{
    public static LibrarySnapshot Create()
    {
        var design = new FeedCategory("demo/design", "Design & culture");
        var outdoors = new FeedCategory("demo/outdoors", "The world outside");
        var technology = new FeedCategory("demo/technology", "Technology");
        List<Subscription> feeds = [
            new("demo/fieldnotes", "Field Notes", [outdoors]),
            new("demo/considered", "The Considered Life", [design]),
            new("demo/studio", "Studio Journal", [design]),
            new("demo/openweb", "The Open Web", [technology]),
            new("demo/observatory", "Observatory", [outdoors]),
            new("demo/margins", "In the Margins", [design]),
            new("demo/signal", "Signal & Noise", [technology])
        ];
        var now = DateTimeOffset.Now;
        var articles = new List<Article>
        {
            Story("1", feeds[0], "A slower way to see the world", "Maya Ellis", "landscape.jpg", now.AddMinutes(-32),
                "<p>There is a particular kind of quiet you find at the edge of a lake, just before the rest of the world wakes up. The mountains haven't moved. The water is still. For once, there is nowhere else you need to be.</p><p>We spend so much of our time looking for the next thing that we forget how much there is in front of us. A path we have walked a hundred times. The changing light on a familiar hillside. A conversation that has room to wander.</p><h2>Attention is a kind of generosity</h2><p>On this trip, I left the itinerary at home. Each morning began with a walk and a question: <em>what would I notice if I weren't in a hurry?</em></p><p>The answer was rarely spectacular. It was a bird working its way along the shoreline, a cloud caught between two peaks, the sound of gravel underfoot. Small things, given enough time, become the whole point.</p><blockquote>Sometimes the best thing a journey can give you is permission to stand still.</blockquote><p>Perhaps a slower way of seeing begins here: with one place, one moment, and the willingness to pay attention.</p>"),
            Story("2", feeds[1], "The things we choose to keep", "Oliver Chen", null, now.AddHours(-2),
                "<p>A well-used notebook. A chair that has followed you through three apartments. A book with someone else's handwriting in the margin.</p><p>The objects that matter tend to earn their place slowly. Their value comes from the lives they have been part of.</p><h2>Making room for meaning</h2><p>Keeping fewer things can be less about restraint and more about attention. When everything has a story, an ordinary room becomes a record of a life.</p>"),
            Story("3", feeds[2], "Good design leaves room to breathe", "Alex Morgan", "forest.jpg", now.AddHours(-3),
                "<p>Space is an active part of a composition. It gives an idea somewhere to land and a reader somewhere to pause.</p><p>The best interfaces understand this. They are generous with what matters and economical with everything that gets in the way.</p><h2>A useful kind of quiet</h2><p>Remove a line. Shorten a label. Let a heading carry its own weight. Each small decision gives the work a little more clarity.</p>"),
            Story("4", feeds[3], "A small website is still a wonderful thing", "Sam Rivera", null, now.AddHours(-5),
                "<p>Somewhere on the web, someone is keeping a list of their favorite trees. Someone else is writing about repairing an old radio.</p><p>This is the web I keep returning to: personal, curious, and made by people who have something they want to share.</p><p>RSS gives these small places a way to stay connected. A new post arrives, quietly, in a reader you chose.</p>"),
            Story("5", feeds[4], "Where the mountains meet the morning", "Maya Ellis", "mountains.jpg", now.AddHours(-7),
                "<p>Before sunrise, the valley is all blue shadow. The first light arrives at the highest ridge, then works its way down, slowly revealing a landscape that feels entirely new.</p><p>We waited with a flask of coffee and no particular plan. It was enough.</p>"),
            Story("6", feeds[5], "On reading one thing at a time", "Jamie Park", null, now.AddDays(-1),
                "<p>Reading asks for an unusual kind of trust: that the next sentence is worth staying for.</p><p>I have been trying to give the things I read a little more time. One article. One essay. A chapter with the phone in another room.</p><p>Some days that is all the practice I need.</p>"),
            Story("7", feeds[6], "Tools that get out of the way", "Sam Rivera", null, now.AddDays(-1).AddHours(-3),
                "<p>A good tool makes its purpose obvious. A great one becomes familiar enough that you stop thinking about it.</p><p>Software can have that quality too: a handful of considered actions, predictable behavior, and the feeling that someone has thought about the details.</p>")
        };
        articles[5].IsSaved = true;
        return new() { UserId = "demo", UserName = "Demo library", Articles = articles, Subscriptions = feeds };
    }

    private static Article Story(string id, Subscription feed, string title, string author, string? image, DateTimeOffset date, string body)
    {
        var imageUrl = image is null ? "" : "ms-appx:///Assets/Images/" + image;
        var photoSource = image switch
        {
            "landscape.jpg" => "https://images.unsplash.com/photo-1470770841072-f978cf4d019e",
            "forest.jpg" => "https://images.unsplash.com/photo-1441974231531-c6227db76b6e",
            _ => "https://images.unsplash.com/photo-1501785888041-af3ef285b470"
        };
        return new() { Id = "demo/" + id, FeedId = feed.Id, FeedTitle = feed.Title, Title = title, Author = author,
            Published = date, ImageUrl = imageUrl, Url = "", IsUnread = true,
            Html = (image is null ? "" : $"<div><a href=\"{photoSource}\"><img src=\"{imageUrl}\" alt=\"A quiet landscape\" /></a></div>") + body };
    }
}
