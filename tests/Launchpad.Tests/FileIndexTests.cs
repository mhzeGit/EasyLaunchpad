using Launchpad.Core.Index;

namespace Launchpad.Tests;

public class FileIndexTests
{
    private static (FileIndex ix, int root, int docs, int file) Sample()
    {
        var ix = new FileIndex(16);
        int root = ix.Add(FileIndex.NoParent, "C:", EntryFlags.Directory, 0, 0);
        int docs = ix.Add(root, "Docs", EntryFlags.Directory, 0, 0);
        int file = ix.Add(docs, "Report.PDF", EntryFlags.None, 1234, 99);
        return (ix, root, docs, file);
    }

    [Fact]
    public void BuildsPathsAndResolvesCaseInsensitively()
    {
        var (ix, root, docs, file) = Sample();
        Assert.Equal(@"C:\", ix.GetPath(root));
        Assert.Equal(@"C:\Docs\Report.PDF", ix.GetPath(file));
        Assert.Equal(file, ix.Resolve(@"c:\docs\report.pdf"));
        Assert.Equal(docs, ix.Resolve(@"C:\Docs\"));
        Assert.Equal(-1, ix.Resolve(@"C:\Nope"));
        Assert.Equal("pdf", ix.Extensions[ix.Ext[file]]);
    }

    [Fact]
    public void RenamingAFolderMovesItsChildren()
    {
        var (ix, root, docs, file) = Sample();
        ix.Rename(docs, root, "Papers");
        Assert.Equal(@"C:\Papers\Report.PDF", ix.GetPath(file));
        Assert.Equal(-1, ix.Resolve(@"C:\Docs\Report.PDF"));
        Assert.Equal(file, ix.Resolve(@"C:\Papers\Report.PDF"));
    }

    [Fact]
    public void DeletingAFolderOrphansChildrenAndCompactionDropsThem()
    {
        var (ix, root, docs, file) = Sample();
        ix.Remove(docs);
        Assert.False(ix.IsLive(file));
        int removed = ix.Compact();
        Assert.Equal(2, removed);
        Assert.Equal(1, ix.Count);
        Assert.Equal(root, ix.Resolve("C:"));
    }

    [Fact]
    public void UpsertUpdatesMetadataInPlace()
    {
        var (ix, _, docs, file) = Sample();
        int again = ix.Upsert(docs, "report.pdf", EntryFlags.None, 5000, 123, out bool added);
        Assert.False(added);
        Assert.Equal(file, again);
        Assert.Equal(5000, ix.Size[file]);
    }

    [Fact]
    public void SurvivesManyInsertsRemovalsAndTableGrowth()
    {
        var ix = new FileIndex(16);
        int root = ix.Add(FileIndex.NoParent, "D:", EntryFlags.Directory, 0, 0);
        var ids = new List<int>();
        for (int i = 0; i < 50_000; i++) ids.Add(ix.Add(root, $"file{i}.txt", EntryFlags.None, i, i));
        for (int i = 0; i < 50_000; i += 2) ix.Remove(ids[i]);
        for (int i = 0; i < 50_000; i++)
            Assert.Equal(i % 2 == 0 ? -1 : ids[i], ix.Find(root, $"FILE{i}.TXT"));
        // re-adding a removed name works
        int re = ix.Add(root, "file0.txt", EntryFlags.None, 1, 1);
        Assert.Equal(re, ix.Find(root, "file0.txt"));
    }

    [Fact]
    public void SnapshotRoundTrips()
    {
        var (ix, _, _, file) = Sample();
        using var ms = new MemoryStream();
        ix.Save(ms);
        ms.Position = 0;
        var back = FileIndex.Load(ms);
        Assert.Equal(ix.Count, back.Count);
        Assert.Equal(file, back.Resolve(@"C:\Docs\Report.PDF"));
        Assert.Equal(1234, back.Size[file]);
        Assert.Equal("pdf", back.Extensions[back.Ext[file]]);
    }
}
