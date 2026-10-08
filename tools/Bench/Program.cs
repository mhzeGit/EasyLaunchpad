using System.Diagnostics;
using Launchpad.Core.Index;
using Launchpad.Core.Search;
if (args.Length > 0 && args[0] == "--concurrent") { Concurrent.Run(args.Length > 1 ? args[1] : @"C:\"); return; }
if (args.Length > 0 && args[0] == "--classify") { Classify.Run(); return; }

var roots = args.Length > 0 ? args : new[] { @"C:\" };
var ix = new FileIndex(1 << 16);
var opt = new ScanOptions { Roots = roots, ExcludePaths = new[] { @"C:\Windows\WinSxS" } };
long last = 0;
var st = IndexScanner.Reconcile(ix, opt, v => { if (v - last > 250_000) { last = v; Console.Write($"\r  {v:N0} entries..."); } });
Console.WriteLine($"\rFirst scan: {st.Seen:N0} entries seen, {ix.LiveCount:N0} live in {st.Elapsed.TotalSeconds:F1}s   ");
Console.WriteLine($"Memory: {GC.GetTotalMemory(true) / 1048576} MB managed");

st = IndexScanner.Reconcile(ix, opt);
Console.WriteLine($"Re-scan (diff only): {st.Elapsed.TotalSeconds:F1}s  +{st.Added} -{st.Removed} ~{st.Updated}");

string path = Path.Combine(Path.GetTempPath(), "bench.idx");
var sw = Stopwatch.StartNew();
using (var fs = File.Create(path)) ix.Save(fs);
Console.WriteLine($"Save: {sw.ElapsedMilliseconds} ms, {new FileInfo(path).Length / 1048576} MB");
sw.Restart();
FileIndex loaded;
using (var fs = File.OpenRead(path)) loaded = FileIndex.Load(fs);
Console.WriteLine($"Load: {sw.ElapsedMilliseconds} ms ({loaded.LiveCount:N0} entries)");
File.Delete(path);

foreach (string q in new[] { "a", "report", "report pdf", "ext:pdf", "type:image size:>5mb", "*.dll", "regex:^img_[0-9]+", "date:today", "node_modules type:folder", "zzzzqqq" })
{
    var query = QueryParser.Parse(q);
    SearchEngine.Run(loaded, query);   // warm-up
    var times = new List<double>();
    SearchResults r = null!;
    for (int i = 0; i < 5; i++) { r = SearchEngine.Run(loaded, query); times.Add(r.Elapsed.TotalMilliseconds); }
    Console.WriteLine($"  {q,-28} {r.Total,10:N0} hits  best {times.Min(),6:F1} ms   top: {r.Hits.FirstOrDefault()?.Path}");
}
