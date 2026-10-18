using Unread.Core;
using Xunit;

namespace Unread.Core.Tests;

public sealed class ArticleStreamStateTests
{
    [Fact]
    public void Switching_sources_clears_the_old_cursor_and_old_results_cannot_restore_it()
    {
        var state = new ArticleStreamState();
        var oldSource = state.Reset();
        state.TrySetContinuation(oldSource, "old-source-page-2");
        var newSource = state.Reset();
        Assert.Null(state.Continuation);
        Assert.False(state.TrySetContinuation(oldSource, "late-old-cursor"));
        Assert.True(state.TrySetContinuation(newSource, "new-source-page-2"));
        Assert.False(state.TrySetContinuation(oldSource, null));
        Assert.Equal("new-source-page-2", state.Continuation);
    }

    [Fact]
    public void A_cancelled_request_finishing_cannot_unlock_a_newer_request()
    {
        var state = new ArticleStreamState();
        state.Reset();
        var oldLoad = state.BeginLoad();
        state.Reset();
        var newLoad = state.BeginLoad();
        state.EndLoad(oldLoad);
        Assert.True(state.IsLoading);
        Assert.False(state.IsCurrent(oldLoad));
        state.EndLoad(newLoad);
        Assert.False(state.IsLoading);
    }

    [Fact]
    public void Refresh_completion_only_sets_paging_for_the_source_it_requested()
    {
        var state = new ArticleStreamState();
        var refreshSource = state.Reset();
        // A global refresh has no source-load ownership; switching sources stays possible.
        Assert.False(state.IsLoading);
        state.Reset();
        var navigation = state.BeginLoad();
        Assert.True(state.TrySetContinuation(navigation, "current-page-2"));
        Assert.False(state.TrySetContinuation(refreshSource, "refresh-page-2"));
        state.EndLoad(refreshSource);
        Assert.True(state.IsLoading);
        Assert.Equal("current-page-2", state.Continuation);
        state.EndLoad(navigation);
        Assert.False(state.IsLoading);
    }

    [Fact]
    public void Replacing_a_source_with_cached_content_does_not_wait_for_the_cancelled_network_request()
    {
        var state = new ArticleStreamState();
        var oldLoad = state.BeginLoad();
        state.Reset();
        Assert.False(state.IsLoading);
        Assert.Null(state.Continuation);
        state.EndLoad(oldLoad);
        Assert.False(state.IsLoading);
        var currentLoad = state.BeginLoad();
        Assert.True(state.TrySetContinuation(currentLoad, null));
        state.EndLoad(currentLoad);
        Assert.False(state.IsLoading);
    }
}
