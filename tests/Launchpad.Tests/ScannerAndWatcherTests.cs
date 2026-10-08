using Launchpad.Core.Index;
using Launchpad.Core.Search;

namespace Launchpad.Tests;

public sealed class TempTree : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "lp-test-" + Guid.NewGuid().ToString("N")[..8]);
    public TempTree()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.Combine(Root, "a", "deep"));
        Directory.CreateDirectory(Path.Combine(Root, "b"));
        Directory.CreateDirectory(Path.Combine(Root, "$Recycle.Bin"));
        File.WriteAllText(Path.Combine(Root, "a", "one.txt"), "hello");
        File.WriteAllText(Path.Combine(Root, "a", "deep", "two.log"), "hello world");
        File.WriteAllText(Path.Combine(Root, "b", "three.txt"), "");
        File.WriteAllText(Path.Combine(Root, "$Recycle.Bin", "ghost.txt"), "x");
    }
    public string P(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());
    public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
}

public class ScannerTests
{
    // The temp root isn't a volume root, so index it as a pseudo-root by scanning its parent chain via Resolve-free API:
    // we register the temp folder itself as the "root" name and let the scanner walk it.
    private static (FileIndex ix, ScanOptions opt) Setup(TempTree t)
    {
        var ix = new FileIndex(16);
        return (ix, new ScanOptions { Roots = new[] { t.Root }, Parallelism = 4 });
    }

    private static List<string> Paths(FileIndex ix, string q)
    {
        var res = SearchEngine.Run(ix, QueryParser.Parse(q), new SearchOptions { Sort = SortKey.Path, Limit = 1000 });
        return res.Hits.Select(h => h.Path).ToList();
    }

    [Fact]
    public void FirstScanIndexesEverythingExceptExclusions()
    {
        using var t = new TempTree();
        var (ix, opt) = Setup(t);
        var st = IndexScanner.Reconcile(ix, opt);
        Assert.True(st.Completed);

        Assert.Contains(t.P("a", "one.txt"), Paths(ix, "one.txt"));
        Assert.Contains(t.P("a", "deep", "two.log"), Paths(ix, "ext:log"));
        Assert.Empty(Paths(ix, "ghost"));
        Assert.Equal(11, ix.Size[ix.Resolve(t.P("a", "deep", "two.log"))]);
    }

    [Fact]
    public void SecondScanPicksUpAddsRemovesAndChangesWithoutDuplicates()
    {
        using var t = new TempTree();
        var (ix, opt) = Setup(t);
        IndexScanner.Reconcile(ix, opt);
        int before = ix.LiveCount;

        File.Delete(t.P("b", "three.txt"));
        File.WriteAllText(t.P("b", "four.txt"), "12345678");
        File.WriteAllText(t.P("a", "one.txt"), "now much longer content");
        Directory.Delete(t.P("a", "deep"), true);

        var st = IndexScanner.Reconcile(ix, opt);
        Assert.True(st.Completed);
        Assert.Equal(1, st.Added);       // four.txt
        Assert.Equal(3, st.Removed);     // three.txt, deep, two.log
        Assert.Equal(before - 3 + 1, ix.LiveCount);
        Assert.Empty(Paths(ix, "three"));
        Assert.Single(Paths(ix, "four"));
        Assert.Equal(23, ix.Size[ix.Resolve(t.P("a", "one.txt"))]);
        Assert.Single(Paths(ix, "one.txt"));
    }

    [Fact]
    public void ExcludedPathsAreSkipped()
    {
        using var t = new TempTree();
        var ix = new FileIndex(16);
        IndexScanner.Reconcile(ix, new ScanOptions { Roots = new[] { t.Root }, ExcludePaths = new[] { t.P("a") } });
        Assert.Empty(Paths(ix, "one.txt"));
        Assert.Single(Paths(ix, "three.txt"));
    }

    [Fact]
    public void CancellationLeavesExistingEntriesAlone()
    {
        using var t = new TempTree();
        var (ix, opt) = Setup(t);
        IndexScanner.Reconcile(ix, opt);
        int before = ix.LiveCount;
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var st = IndexScanner.Reconcile(ix, opt, ct: cts.Token);
        Assert.False(st.Completed);
        Assert.Equal(before, ix.LiveCount);
    }

    [Fact]
    public void HandlesLongPathsAndUnicodeNames()
    {
        using var t = new TempTree();
        string dir = t.Root;
        for (int i = 0; i < 12; i++) dir = Path.Combine(dir, new string('d', 30));
        Directory.CreateDirectory(@"\\?\" + dir);
        File.WriteAllText(@"\\?\" + Path.Combine(dir, "längé-名前.txt"), "x");

        var (ix, opt) = Setup(t);
        IndexScanner.Reconcile(ix, opt);
        var found = Paths(ix, "名前");
        Assert.Single(found);
        Assert.True(found[0].Length > 260);
    }
}

public class WatcherTests
{
    private static (FileIndex ix, ScanOptions opt, IndexWatcher w) Start(TempTree t)
    {
        var ix = new FileIndex(16);
        var opt = new ScanOptions { Roots = new[] { t.Root }, Parallelism = 2 };
        IndexScanner.Reconcile(ix, opt);
        return (ix, opt, new IndexWatcher(ix, opt));
    }

    [Fact]
    public void AppliesCreateRenameDeleteAndMoves()
    {
        using var t = new TempTree();
        var (ix, _, w) = Start(t);
        using var _ = w;

        File.WriteAllText(t.P("b", "new.md"), "hi");
        w.ProcessForTest(new[] { ("Created", t.P("b", "new.md"), (string?)null) });
        Assert.True(ix.Resolve(t.P("b", "new.md")) >= 0);

        File.Move(t.P("b", "new.md"), t.P("b", "renamed.md"));
        w.ProcessForTest(new[] { ("Renamed", t.P("b", "renamed.md"), (string?)t.P("b", "new.md")) });
        Assert.True(ix.Resolve(t.P("b", "new.md")) < 0);
        Assert.True(ix.Resolve(t.P("b", "renamed.md")) >= 0);

        // moving a whole folder keeps its children reachable
        Directory.Move(t.P("a"), t.P("b", "a-moved"));
        w.ProcessForTest(new[] { ("Renamed", t.P("b", "a-moved"), (string?)t.P("a")) });
        Assert.True(ix.Resolve(t.P("b", "a-moved", "deep", "two.log")) >= 0);
        Assert.True(ix.Resolve(t.P("a", "one.txt")) < 0);

        File.Delete(t.P("b", "renamed.md"));
        w.ProcessForTest(new[] { ("Deleted", t.P("b", "renamed.md"), (string?)null) });
        Assert.True(ix.Resolve(t.P("b", "renamed.md")) < 0);
    }

    [Fact]
    public void ChangedEventsRefreshSize_AndNewFoldersAreScanned()
    {
        using var t = new TempTree();
        var (ix, _, w) = Start(t);
        using var _ = w;

        File.WriteAllText(t.P("b", "three.txt"), "0123456789");
        w.ProcessForTest(new[] { ("Changed", t.P("b", "three.txt"), (string?)null) });
        Assert.Equal(10, ix.Size[ix.Resolve(t.P("b", "three.txt"))]);

        // a folder copied in with contents arrives as one Created event
        Directory.CreateDirectory(t.P("c", "inner"));
        File.WriteAllText(t.P("c", "inner", "x.dat"), "x");
        w.ProcessForTest(new[] { ("Created", t.P("c"), (string?)null) });
        Assert.True(ix.Resolve(t.P("c", "inner", "x.dat")) >= 0);
    }

    [Fact]
    public void IgnoresExcludedLocations()
    {
        using var t = new TempTree();
        var (ix, _, w) = Start(t);
        using var _ = w;
        File.WriteAllText(t.P("$Recycle.Bin", "later.txt"), "x");
        w.ProcessForTest(new[] { ("Created", t.P("$Recycle.Bin", "later.txt"), (string?)null) });
        Assert.True(ix.Resolve(t.P("$Recycle.Bin", "later.txt")) < 0);
    }

    [Fact]
    public async Task LiveWatcherPicksUpRealFileSystemChanges()
    {
        using var t = new TempTree();
        var (ix, _, w) = Start(t);
        using var _ = w;
        w.Debounce = TimeSpan.FromMilliseconds(50);
        w.Start();
        w.Resume();

        File.WriteAllText(t.P("b", "live.txt"), "abc");
        await WaitFor(() => { using var r = ix.ReadLock(); return ix.Resolve(t.P("b", "live.txt")) >= 0; });

        File.Move(t.P("b", "live.txt"), t.P("b", "live2.txt"));
        await WaitFor(() => { using var r = ix.ReadLock(); return ix.Resolve(t.P("b", "live2.txt")) >= 0 && ix.Resolve(t.P("b", "live.txt")) < 0; });

        File.Delete(t.P("b", "live2.txt"));
        await WaitFor(() => { using var r = ix.ReadLock(); return ix.Resolve(t.P("b", "live2.txt")) < 0; });
    }

    private static async Task WaitFor(Func<bool> cond, int timeoutMs = 8000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return;
            await Task.Delay(50);
        }
        Assert.Fail("Condition not met within timeout");
    }
}
