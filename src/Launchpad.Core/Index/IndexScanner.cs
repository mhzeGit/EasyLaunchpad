using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Launchpad.Core.Index;

public sealed class ScanOptions
{
    /// <summary>Volume roots such as <c>C:\</c>.</summary>
    public IReadOnlyList<string> Roots { get; init; } = Array.Empty<string>();

    /// <summary>Directory names skipped wherever they appear (case-insensitive).</summary>
    public IReadOnlyCollection<string> ExcludeNames { get; init; } = DefaultExcludeNames;

    /// <summary>Absolute directory paths skipped together with everything below them.</summary>
    public IReadOnlyCollection<string> ExcludePaths { get; init; } = Array.Empty<string>();

    public int Parallelism { get; init; } = Math.Clamp(Environment.ProcessorCount, 2, 8);

    /// <summary>Lower thread and I/O priority so a refresh doesn't compete with foreground work.</summary>
    public bool Background { get; init; }

    public static readonly string[] DefaultExcludeNames =
    {
        "$Recycle.Bin", "System Volume Information", "$WinREAgent", "$SysReset", "Config.Msi", "$Windows.~BT", "$Windows.~WS",
    };
}

public readonly record struct ScanStats(long Seen, int Added, int Removed, int Updated, TimeSpan Elapsed, bool Completed);

/// <summary>
/// Reconciles a <see cref="FileIndex"/> with the file system. The first run on an empty index is a plain scan;
/// later runs diff against what is already stored, so they only touch what actually changed.
/// </summary>
public static class IndexScanner
{
    private readonly record struct Work(int DirId, string Path);

    public static ScanStats Reconcile(FileIndex index, ScanOptions options, Action<long>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var excludeNames = new HashSet<string>(options.ExcludeNames, StringComparer.OrdinalIgnoreCase);
        var excludePaths = new HashSet<string>(options.ExcludePaths.Select(p => p.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase);

        int initialCount;
        long[] seen;
        var queue = new BlockingCollection<Work>(new ConcurrentQueue<Work>());
        int pending = 0;

        using (index.WriteLock())
        {
            initialCount = index.Count;
            seen = new long[(initialCount + 63) / 64];
            foreach (string root in options.Roots)
            {
                if (root.TrimEnd('\\').Length == 0 || !Directory.Exists(root)) continue;
                var chain = new List<int>();
                int id = index.EnsurePath(root, chain);
                foreach (int ancestor in chain) MarkSeen(seen, ancestor);   // ids >= initialCount are implicitly seen
                Interlocked.Increment(ref pending);
                queue.Add(new Work(id, root));
            }
        }
        if (pending == 0) return new ScanStats(0, 0, 0, 0, sw.Elapsed, true);

        long visited = 0;
        int added = 0, updated = 0;
        bool cancelled = false;

        void Worker()
        {
            if (options.Background) BeginBackgroundMode();
            var buf = new DirBuffer();
            var children = new List<Work>(64);
            try
            {
                foreach (var work in queue.GetConsumingEnumerable())
                {
                    try
                    {
                        if (ct.IsCancellationRequested) { cancelled = true; continue; }
                        children.Clear();
                        if (Win32Find.Enumerate(work.Path, buf))
                        {
                            int a = 0, u = 0;
                            using (index.WriteLock())
                                ApplyListing(index, work, buf, seen, initialCount, excludeNames, excludePaths, children, ref a, ref u);
                            if (a != 0) Interlocked.Add(ref added, a);
                            if (u != 0) Interlocked.Add(ref updated, u);
                            long v = Interlocked.Add(ref visited, buf.Count);
                            if (progress != null && (v & 0x3FFF) < buf.Count) progress(v);
                            foreach (var c in children)
                            {
                                Interlocked.Increment(ref pending);
                                queue.Add(c);
                            }
                        }
                    }
                    finally
                    {
                        if (Interlocked.Decrement(ref pending) == 0) queue.CompleteAdding();
                    }
                }
            }
            finally { if (options.Background) EndBackgroundMode(); }
        }

        var threads = new Thread[Math.Max(1, options.Parallelism)];
        for (int i = 0; i < threads.Length; i++)
        {
            threads[i] = new Thread(Worker) { IsBackground = true, Name = $"index-scan-{i}" };
            if (options.Background) threads[i].Priority = ThreadPriority.BelowNormal;
            threads[i].Start();
        }
        foreach (var t in threads) t.Join();

        int removed = 0;
        if (!cancelled && !ct.IsCancellationRequested)
        {
            using (index.WriteLock())
            {
                for (int id = 0; id < initialCount; id++)
                {
                    if (IsSeen(seen, id) || index.IsDeleted(id)) continue;
                    index.Remove(id);
                    removed++;
                }
                if (index.NeedsCompaction) index.Compact();
            }
        }
        progress?.Invoke(visited);
        return new ScanStats(visited, added, removed, updated, sw.Elapsed, !cancelled && !ct.IsCancellationRequested);
    }

    /// <summary>Re-scans one folder subtree (used when a folder is created or moved in while the app is running).</summary>
    public static void ScanSubtree(FileIndex index, int dirId, string dirPath, ScanOptions options, CancellationToken ct = default)
    {
        var excludeNames = new HashSet<string>(options.ExcludeNames, StringComparer.OrdinalIgnoreCase);
        var excludePaths = new HashSet<string>(options.ExcludePaths.Select(p => p.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<Work>();
        stack.Push(new Work(dirId, dirPath));
        var buf = new DirBuffer();
        var children = new List<Work>();
        long[] noSeen = Array.Empty<long>();
        while (stack.Count > 0 && !ct.IsCancellationRequested)
        {
            var work = stack.Pop();
            children.Clear();
            if (!Win32Find.Enumerate(work.Path, buf)) continue;
            int a = 0, u = 0;
            using (index.WriteLock())
                ApplyListing(index, work, buf, noSeen, 0, excludeNames, excludePaths, children, ref a, ref u);
            foreach (var c in children) stack.Push(c);
        }
    }

    private static void ApplyListing(FileIndex index, Work work, DirBuffer buf, long[] seen, int initialCount,
        HashSet<string> excludeNames, HashSet<string> excludePaths, List<Work> children, ref int added, ref int updated)
    {
        string dirPath = work.Path;
        string prefix = dirPath.EndsWith('\\') ? dirPath : dirPath + "\\";

        // Entries from a previous run that sit under this directory but aren't listed any more are
        // swept at the end of Reconcile (they're never marked seen), so nothing to do for them here.
        for (int i = 0; i < buf.Count; i++)
        {
            ref readonly DirEntry e = ref buf.Items[i];
            ReadOnlySpan<char> name = buf.Name(i);
            bool isDir = e.IsDirectory;

            if (isDir && (excludeNames.Contains(name.ToString()) ||
                          (excludePaths.Count > 0 && excludePaths.Contains(prefix + name.ToString()))))
                continue;

            EntryFlags flags = e.ToFlags();
            int id = index.Find(work.DirId, name);
            if (id >= 0 && index.IsDirectory(id) != isDir)
            {
                index.Remove(id);   // a file was replaced by a folder of the same name (or vice versa)
                id = -1;
            }

            if (id < 0)
            {
                id = index.Add(work.DirId, name, flags, e.Size, e.Mtime);
                added++;
            }
            else
            {
                if (index.SetMeta(id, flags, e.Size, e.Mtime)) updated++;
                if (id < initialCount) MarkSeen(seen, id);
            }

            if (isDir && !e.IsReparse)
                children.Add(new Work(id, prefix + name.ToString()));
        }
    }

    private static void MarkSeen(long[] seen, int id)
    {
        if (id >> 6 >= seen.Length) return;   // newly added entry: implicitly seen
        Interlocked.Or(ref seen[id >> 6], 1L << (id & 63));
    }

    private static bool IsSeen(long[] seen, int id) => (seen[id >> 6] & (1L << (id & 63))) != 0;

    // ---- background (low I/O + memory priority) mode for the current thread ----

    private const int ThreadModeBackgroundBegin = 0x00010000, ThreadModeBackgroundEnd = 0x00020000;

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern bool SetThreadPriority(IntPtr thread, int priority);

    private static void BeginBackgroundMode() => SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin);
    private static void EndBackgroundMode() => SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundEnd);
}
