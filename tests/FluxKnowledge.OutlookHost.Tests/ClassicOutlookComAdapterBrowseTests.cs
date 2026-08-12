using FluxKnowledge.OutlookHost;
using Xunit;

namespace FluxKnowledge.OutlookHost.Tests;

public sealed class ClassicOutlookComAdapterBrowseTests
{
    [Fact]
    public void Adapter_directed_traversal_resolves_only_the_complete_case_insensitive_path_and_releases_every_com_value()
    {
        var released = new List<object?>();
        var roots = new FolderCollection(
            Node("Mailbox", "store-1", "root", Node("Action", "store-1", "action")),
            Node("Archive", "store-2", "archive", Node("Action", "store-2", "other-action")));

        var result = Resolve(roots, "mailbox/aCtIoN", 500, released);

        Assert.Equal(new ClassicOutlookComAdapter.BrowseFolderIdentity("store-1", "action", "Action"), result);
        Assert.NotEmpty(released);
        Assert.Contains(roots[0], released);
        Assert.Contains(roots[0].Children, released);
    }

    [Fact]
    public void Adapter_directed_traversal_rejects_ambiguous_complete_segments()
    {
        var roots = new FolderCollection(
            Node("Mailbox", "store-1", "root-a", Node("Action", "store-1", "action-a")),
            Node("Mailbox", "store-2", "root-b", Node("Action", "store-2", "action-b")));

        Assert.Throws<OutlookBrowseTargetException>(() => Resolve(roots, "Mailbox/Action", 500, []));
    }

    [Fact]
    public void Adapter_directed_traversal_rejects_a_missing_complete_target()
    {
        var roots = new FolderCollection(Node("Mailbox", "store", "root", Node("Inbox", "store", "inbox")));

        Assert.Throws<OutlookBrowseTargetException>(() => Resolve(roots, "Mailbox/Action", 500, []));
    }

    [Fact]
    public void Adapter_directed_traversal_stops_at_the_candidate_bound_for_an_unrelated_tree()
    {
        var roots = new FolderCollection(Enumerable.Range(0, 501)
            .Select(index => Node($"Unrelated-{index}", "store", $"entry-{index}"))
            .ToArray());

        Assert.Throws<OutlookBrowseTargetException>(() => Resolve(roots, "Mailbox/Action", 500, []));
    }

    [Fact]
    public void Adapter_directed_traversal_resolves_the_target_without_entering_an_unrelated_large_subtree()
    {
        var unrelatedChildren = Enumerable.Range(0, 501)
            .Select(index => Node($"Unrelated-{index}", "other", $"entry-{index}"))
            .ToArray();
        var roots = new FolderCollection(
            Node("Mailbox", "store", "root", Node("Action", "store", "action")),
            Node("Other", "other", "other-root", unrelatedChildren));

        var result = Resolve(roots, "Mailbox/Action", 500, []);

        Assert.Equal("action", result.EntryId);
    }

    private static ClassicOutlookComAdapter.BrowseFolderIdentity Resolve(FolderCollection root, string targetPath, int bound, List<object?> released) =>
        DirectedOutlookFolderTraversal.Resolve(
            root,
            targetPath.Split('/'),
            bound,
            static folders => folders.Count,
            static (folders, index) => folders[index - 1],
            static folder => folder.Name,
            static folder => folder.Children,
            static folder => new ClassicOutlookComAdapter.BrowseFolderIdentity(folder.StoreId, folder.EntryId, folder.Name),
            released.Add,
            CancellationToken.None);

    private static FolderNode Node(string name, string storeId, string entryId, params FolderNode[] children) =>
        new(name, storeId, entryId, new FolderCollection(children));

    private sealed class FolderCollection(params FolderNode[] folders)
    {
        public int Count => folders.Length;
        public FolderNode this[int index] => folders[index];
    }

    private sealed record FolderNode(string Name, string StoreId, string EntryId, FolderCollection Children);
}
