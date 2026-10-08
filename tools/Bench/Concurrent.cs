using System.Diagnostics;
using Launchpad.Core.Index;
using Launchpad.Core.Search;

static class Concurrent
{
    public static void Run(string root)
    {
        var ix = new FileIndex(1 << 16);
        var opt = new ScanOptions { Roots = new[] { root }, ExcludePaths = new[] { @"C:\Windows\WinSxS" } };
        var scan = Task.Run(() => IndexScanner.Reconcile(ix, opt));
        var lat = new List<double>();
        var sw = Stopwatch.StartNew();
        var q = QueryParser.Parse("report");
        while (!scan.IsCompleted)
        {
            var t = Stopwatch.StartNew();
            var r = SearchEngine.Run(ix, q, new SearchOptions { Limit = 200 });
            lat.Add(t.Elapsed.TotalMilliseconds);
            Thread.Sleep(50);
        }
        lat.Sort();
        Console.WriteLine($"scan {sw.Elapsed.TotalSeconds:F1}s, {ix.LiveCount:N0} entries; {lat.Count} searches during scan:");
        Console.WriteLine($"  median {lat[lat.Count / 2]:F1} ms   p95 {lat[(int)(lat.Count * .95)]:F1} ms   max {lat[^1]:F1} ms");
    }
}
