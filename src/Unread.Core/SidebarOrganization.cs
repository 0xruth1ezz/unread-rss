namespace Unread.Core;

public sealed record SidebarEntry(string Id, string Title, bool IsFolder, string ParentId);

/// <summary>Local folders and per-parent ordering. Root subscriptions use an empty parent ID.</summary>
public static class SidebarOrganization
{
    public const int MaximumDepth = 64;

    // Older libraries stored folders only on subscriptions. Materialize parents and empty folders,
    // keeping existing identities so renames and moves never invalidate the selected folder.
    public static void Normalize(LibrarySnapshot library)
    {
        var folders = new List<FeedCategory>();
        FeedCategory Ensure(string[] path, string? preferredId = null)
        {
            if (path.Length == 0 || path.Length > MaximumDepth) throw new FormatException("Folders support up to 64 levels.");
            var found = folders.FirstOrDefault(f => SamePath(f.Path, path));
            if (found is not null) return found;
            if (path.Length > 1) Ensure(path[..^1]);
            var folder = LocalLibrary.Folder(path);
            if (!string.IsNullOrEmpty(preferredId) && folders.All(f => f.Id != preferredId)) folder = folder with { Id = preferredId };
            // A path-derived ID can belong to a folder that has since been renamed.
            if (folders.Any(f => f.Id == folder.Id)) folder = folder with { Id = "local:folder:" + Guid.NewGuid().ToString("N") };
            folders.Add(folder);
            return folder;
        }
        foreach (var folder in library.Folders.OrderBy(f => PathOf(f).Length)) Ensure(PathOf(folder), folder.Id);
        for (var i = 0; i < library.Subscriptions.Count; i++)
        {
            var feed = library.Subscriptions[i];
            library.Subscriptions[i] = feed with
            { Categories = feed.Categories.Select(c => Ensure(PathOf(c), c.Id)).DistinctBy(c => c.Id).ToList() };
        }
        library.Folders = folders;
    }

    public static IReadOnlyList<SidebarEntry> Children(LibrarySnapshot library, string parentId = "")
    {
        var parent = ParentPath(library, parentId);
        var entries = library.Folders.Where(f => SamePath(f.Path[..^1], parent))
            .Select(f => new SidebarEntry(f.Id, f.Path[^1], true, parentId))
            .Concat(library.Subscriptions.Where(f => parentId.Length == 0 ? f.Categories.Count == 0 : f.Categories.Any(c => c.Id == parentId))
                .Select(f => new SidebarEntry(f.Id, f.Title, false, parentId)))
            .OrderByDescending(e => e.IsFolder).ThenBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        var order = library.SidebarOrder.GetValueOrDefault(parentId) ?? [];
        var positions = order.Select((id, index) => (id, index)).DistinctBy(p => p.id).ToDictionary(p => p.id, p => p.index);
        return entries.OrderBy(e => positions.GetValueOrDefault(e.Id, int.MaxValue)).ToList();
    }

    public static HashSet<string> FeedIds(LibrarySnapshot library, string key)
    {
        if (library.Folders.FirstOrDefault(f => f.Id == key) is not { } folder) return [key];
        var folders = library.Folders.Where(f => StartsWith(f.Path, folder.Path)).Select(f => f.Id).ToHashSet();
        return library.Subscriptions.Where(s => s.Categories.Any(c => folders.Contains(c.Id))).Select(s => s.Id).ToHashSet();
    }

    public static FeedCategory CreateFolder(LibrarySnapshot library, string name, string parentId = "")
    {
        Normalize(library);
        var path = ParentPath(library, parentId).Append(Name(name)).ToArray();
        ValidatePath(library, path);
        var folder = LocalLibrary.Folder(path);
        if (library.Folders.Any(f => f.Id == folder.Id)) folder = folder with { Id = "local:folder:" + Guid.NewGuid().ToString("N") };
        var order = Children(library, parentId).Select(e => e.Id).ToList();
        library.Folders.Add(folder); order.Add(folder.Id); library.SidebarOrder[parentId] = order;
        return folder;
    }

    public static void RenameFolder(LibrarySnapshot library, string id, string name)
    {
        Normalize(library);
        var folder = Folder(library, id);
        ChangePath(library, folder, [.. folder.Path[..^1], Name(name)]);
    }

    public static bool Move(LibrarySnapshot library, string id, string sourceParent, string destinationParent,
        string? relativeTo = null, bool after = false)
    {
        Normalize(library);
        var source = Children(library, sourceParent).ToList();
        var entry = source.FirstOrDefault(e => e.Id == id) ?? throw new InvalidOperationException("This item has moved. Try again.");
        var destination = Children(library, destinationParent).ToList();
        if (relativeTo == id && sourceParent == destinationParent) return false;
        if (relativeTo is not null && destination.All(e => e.Id != relativeTo)) throw new InvalidOperationException("The drop position has changed. Try again.");
        if (entry.IsFolder)
        {
            var folder = Folder(library, id);
            var parentPath = ParentPath(library, destinationParent);
            if (StartsWith(parentPath, folder.Path)) throw new InvalidOperationException("A folder cannot be moved inside itself or its subfolders.");
            var newPath = parentPath.Append(folder.Path[^1]).ToArray();
            ValidateSubtreePath(library, folder, newPath);
        }
        var sourceOrder = source.Select(e => e.Id).Where(key => key != id).ToList();
        var destinationOrder = destination.Select(e => e.Id).Where(key => key != id).ToList();
        var index = relativeTo is null ? destinationOrder.Count : destinationOrder.IndexOf(relativeTo) + (after ? 1 : 0);
        destinationOrder.Insert(index, id);
        if (sourceParent == destinationParent && source.Select(e => e.Id).SequenceEqual(destinationOrder)) return false;

        if (entry.IsFolder && sourceParent != destinationParent)
        {
            var folder = Folder(library, id);
            ChangePath(library, folder, [.. ParentPath(library, destinationParent), folder.Path[^1]]);
        }
        else if (!entry.IsFolder && sourceParent != destinationParent)
        {
            var feedIndex = library.Subscriptions.FindIndex(s => s.Id == id);
            var feed = library.Subscriptions[feedIndex];
            var categories = destinationParent.Length == 0 ? [] : feed.Categories.Where(c => c.Id != sourceParent).ToList();
            if (destinationParent.Length > 0 && categories.All(c => c.Id != destinationParent)) categories.Add(Folder(library, destinationParent));
            library.Subscriptions[feedIndex] = feed with { Categories = categories };
        }
        library.SidebarOrder[sourceParent] = sourceOrder;
        library.SidebarOrder[destinationParent] = destinationOrder;
        return true;
    }

    public static void RemoveFolder(LibrarySnapshot library, string id)
    {
        Normalize(library);
        var folder = Folder(library, id);
        var parent = library.Folders.FirstOrDefault(f => SamePath(f.Path, folder.Path[..^1]));
        var parentId = parent?.Id ?? "";
        var oldOrder = Children(library, parentId).Select(e => e.Id).ToList();
        var position = oldOrder.IndexOf(id);
        var removed = library.Folders.Where(f => StartsWith(f.Path, folder.Path)).Select(f => f.Id).ToHashSet();
        IEnumerable<string> FeedsInOrder(string parentFolder)
        {
            foreach (var entry in Children(library, parentFolder))
            {
                if (entry.IsFolder) { foreach (var feed in FeedsInOrder(entry.Id)) yield return feed; }
                else yield return entry.Id;
            }
        }
        var movedFeeds = FeedsInOrder(id).Distinct().ToList();
        for (var i = 0; i < library.Subscriptions.Count; i++)
        {
            var feed = library.Subscriptions[i];
            if (!movedFeeds.Contains(feed.Id)) continue;
            var categories = feed.Categories.Where(c => !removed.Contains(c.Id)).ToList();
            if (parent is not null && categories.All(c => c.Id != parentId)) categories.Add(parent);
            library.Subscriptions[i] = feed with { Categories = categories };
        }
        library.Folders.RemoveAll(f => removed.Contains(f.Id));
        foreach (var key in removed) library.SidebarOrder.Remove(key);
        var visible = Children(library, parentId).Select(e => e.Id).ToHashSet();
        oldOrder.RemoveAll(key => key == id || movedFeeds.Contains(key) || !visible.Contains(key));
        oldOrder.InsertRange(Math.Clamp(position, 0, oldOrder.Count), movedFeeds.Where(visible.Contains));
        library.SidebarOrder[parentId] = oldOrder.Distinct().ToList();
    }

    public static int Import(LibrarySnapshot library, OpmlImport imported)
    {
        Normalize(library);
        // Existing manual order wins; newly imported items retain their OPML ordering.
        foreach (var parent in library.Folders.Select(f => f.Id).Prepend("").ToList())
            library.SidebarOrder[parent] = Children(library, parent).Select(e => e.Id).ToList();
        library.Folders.AddRange(imported.Folders.Where(f => library.Folders.All(existing => !SamePath(existing.Path, f.Path))));
        var added = LocalLibrary.AddSubscriptions(library, imported.Subscriptions);
        string CanonicalId(string id) => imported.Folders.FirstOrDefault(f => f.Id == id) is { } folder
            ? library.Folders.First(f => SamePath(f.Path, folder.Path)).Id : id;
        foreach (var (parent, order) in imported.SidebarOrder)
        {
            var key = parent.Length == 0 ? "" : CanonicalId(parent);
            library.SidebarOrder[key] = (library.SidebarOrder.GetValueOrDefault(key) ?? []).Concat(order.Select(CanonicalId)).Distinct().ToList();
        }
        return added;
    }

    public static LibrarySnapshot CopyForEdit(LibrarySnapshot library) => new()
    {
        UserId = library.UserId, UserName = library.UserName, LastSync = library.LastSync,
        Articles = library.Articles, Pending = library.Pending, UnreadCounts = library.UnreadCounts,
        Subscriptions = [.. library.Subscriptions], Folders = [.. library.Folders],
        SidebarOrder = library.SidebarOrder.ToDictionary(pair => pair.Key, pair => new List<string>(pair.Value))
    };

    private static FeedCategory Folder(LibrarySnapshot library, string id) => library.Folders.FirstOrDefault(f => f.Id == id)
        ?? throw new InvalidOperationException("This folder no longer exists.");
    private static string[] ParentPath(LibrarySnapshot library, string id) => id.Length == 0 ? [] : Folder(library, id).Path;
    private static string[] PathOf(FeedCategory folder) => folder.Path.Length > 0 ? folder.Path : [folder.Label];
    private static bool SamePath(string[] left, string[] right) => left.SequenceEqual(right, StringComparer.OrdinalIgnoreCase);
    private static bool StartsWith(string[] path, string[] prefix) => path.Length >= prefix.Length && SamePath(path[..prefix.Length], prefix);
    private static string Name(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Length > 100 || name.Contains('/') || name.Any(char.IsControl))
            throw new FormatException("Use a folder name of 1–100 characters without slashes or control characters.");
        return name;
    }
    private static void ValidatePath(LibrarySnapshot library, string[] path, HashSet<string>? excluded = null)
    {
        if (path.Length > MaximumDepth) throw new FormatException("Folders support up to 64 levels.");
        if (library.Folders.Any(f => !(excluded?.Contains(f.Id) ?? false) && SamePath(f.Path, path)))
            throw new InvalidOperationException("A folder with that name already exists here.");
    }
    private static void ValidateSubtreePath(LibrarySnapshot library, FeedCategory folder, string[] newPath)
    {
        var subtree = library.Folders.Where(f => StartsWith(f.Path, folder.Path)).ToList();
        var excluded = subtree.Select(f => f.Id).ToHashSet();
        foreach (var child in subtree) ValidatePath(library, [.. newPath, .. child.Path[folder.Path.Length..]], excluded);
    }
    private static void ChangePath(LibrarySnapshot library, FeedCategory folder, string[] newPath)
    {
        ValidateSubtreePath(library, folder, newPath);
        library.Folders = library.Folders.Select(f => StartsWith(f.Path, folder.Path)
            ? f with { Path = [.. newPath, .. f.Path[folder.Path.Length..]], Label = string.Join(" / ", newPath.Concat(f.Path[folder.Path.Length..])) } : f).ToList();
        var folders = library.Folders.ToDictionary(f => f.Id);
        library.Subscriptions = library.Subscriptions.Select(f => f with { Categories = f.Categories.Select(c => folders[c.Id]).ToList() }).ToList();
    }
}
