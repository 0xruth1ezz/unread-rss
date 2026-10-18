namespace Unread.Core;

/// <summary>UI-thread state that keeps loading and paging attached to the current source.</summary>
public sealed class ArticleStreamState
{
    private long? _loadingVersion;
    public long Version { get; private set; }
    public string? Continuation { get; private set; }
    public bool IsLoading => _loadingVersion is not null;

    public long Reset()
    {
        Version++;
        Continuation = null;
        _loadingVersion = null;
        return Version;
    }

    public long BeginLoad()
    {
        _loadingVersion = Version;
        return Version;
    }

    public bool IsCurrent(long version) => version == Version;

    public bool TrySetContinuation(long version, string? continuation)
    {
        if (!IsCurrent(version)) return false;
        Continuation = continuation;
        return true;
    }

    public void EndLoad(long version)
    {
        if (_loadingVersion == version) _loadingVersion = null;
    }
}
