using System.Collections.Concurrent;

namespace Launchpad.Core.Index;

/// <summary>
/// Keeps a <see cref="FileIndex"/> current by applying file-system notifications in small debounced batches.
/// Creates, deletes and renames are applied in order; the high-volume "changed" notifications are collapsed
/// per path. If the OS drops events (buffer overflow) <see cref="RescanRequested"/> fires so the host can reconcile.
/// </summary>
public sealed class IndexWatcher : IDisposable
{
    private enum Kind { Created, Deleted, Changed, Renamed }
    private readonly record struct FsEvent(Kind Kind, string Path, string? OldPath);

    private readonly FileIndex _index;
    private readonly ScanOptions _options;
    private readonly HashSet<string> _excludeNames;
    private readonly string[] _excludePaths;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly BlockingCollection<FsEvent> _events = new(new ConcurrentQueue<FsEvent>());
    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _gate = new(false);
    private Thread? _thread;

    public TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Raised after a batch has been applied (on the watcher thread).</summary>
    public event Action? BatchApplied;

    /// <summary>Raised when notifications were lost for a root; the host should run <see cref="IndexScanner.Reconcile"/>.</summary>
    public event Action<string>? RescanRequested;

    public IndexWatcher(FileIndex index, ScanOptions options, bool startPaused = true)
    {
        _index = index;
        _options = options;
        _excludeNames = new HashSet<string>(options.ExcludeNames, StringComparer.OrdinalIgnoreCase);
        _excludePaths = options.ExcludePaths.Select(p => p.TrimEnd('\\')).ToArray();
        if (!startPaused) _gate.Set();
    }

    public void Start()
    {
        foreach (string root in _options.Roots)
        {
            try
            {
                var w = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite |
                                   NotifyFilters.Size | NotifyFilters.Attributes,
                };
                w.Created += (_, e) => Enqueue(Kind.Created, e.FullPath, null);
                w.Deleted += (_, e) => Enqueue(Kind.Deleted, e.FullPath, null);
                w.Changed += (_, e) => Enqueue(Kind.Changed, e.FullPath, null);
                w.Renamed += (_, e) => Enqueue(Kind.Renamed, e.FullPath, e.OldFullPath);
                w.Error += (_, _) => RescanRequested?.Invoke(root);
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception) { /* drive vanished or access denied: skip it */ }
        }
        _thread = new Thread(Loop) { IsBackground = true, Name = "index-watcher", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Begin applying events (they are queued while a scan is still running).</summary>
    public void Resume() => _gate.Set();

    private void Enqueue(Kind kind, string path, string? old)
    {
        try { _events.Add(new FsEvent(kind, path, old)); }
        catch (InvalidOperationException) { /* shutting down */ }
    }

    private void Loop()
    {
        var ct = _cts.Token;
        var batch = new List<FsEvent>(256);
        try
        {
            _gate.Wait(ct);
            while (!ct.IsCancellationRequested)
            {
                if (!_events.TryTake(out var first, 500, ct)) continue;
                batch.Clear();
                batch.Add(first);
                var hard = DateTime.UtcNow.AddSeconds(2);
                while (DateTime.UtcNow < hard && _events.TryTake(out var e, (int)Debounce.TotalMilliseconds, ct))
                    batch.Add(e);
                try { Process(batch); } catch (Exception) { /* keep watching */ }
                BatchApplied?.Invoke();
            }
        }
        catch (OperationCanceledException) { }
    }

    // Exposed for deterministic tests.
    internal void ProcessForTest(IReadOnlyList<(string kind, string path, string? old)> events)
    {
        var list = events.Select(e => new FsEvent(Enum.Parse<Kind>(e.kind), e.path, e.old)).ToList();
        Process(list);
    }

    private void Process(List<FsEvent> batch)
    {
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newDirs = new List<(int id, string path)>();

        foreach (var ev in batch)
        {
            switch (ev.Kind)
            {
                case Kind.Created: ApplyPresent(ev.Path, newDirs); break;
                case Kind.Deleted: ApplyDeleted(ev.Path); break;
                case Kind.Renamed: ApplyRenamed(ev.OldPath!, ev.Path, newDirs); break;
                case Kind.Changed: changed.Add(ev.Path); break;
            }
        }
        foreach (string p in changed) ApplyPresent(p, newDirs);

        foreach (var (id, path) in newDirs)
            IndexScanner.ScanSubtree(_index, id, path, _options);

        using (_index.WriteLock())
            if (_index.NeedsCompaction) _index.Compact();
    }

    /// <summary>The path exists on disk: add it or refresh its metadata.</summary>
    private void ApplyPresent(string path, List<(int id, string path)> newDirs)
    {
        if (IsExcluded(path)) return;
        if (!Win32Find.TryStat(path, out uint attr, out long size, out long mtime)) return;

        bool isDir = (attr & Win32Find.AttrDirectory) != 0;
        var flags = new DirEntry { Attr = attr }.ToFlags();
        string? parentPath = Path.GetDirectoryName(path);
        if (parentPath == null) return;
        ReadOnlySpan<char> name = path.AsSpan(parentPath.Length).TrimStart('\\');

        using (_index.WriteLock())
        {
            int parent = _index.Resolve(parentPath);
            if (parent < 0) return;
            int id = _index.Find(parent, name);
            if (id >= 0 && _index.IsDirectory(id) != isDir) { _index.Remove(id); id = -1; }

            if (id < 0)
            {
                id = _index.Add(parent, name, flags, size, mtime);
                if (isDir && (attr & Win32Find.AttrReparse) == 0) newDirs.Add((id, path));
            }
            else if (!isDir)
            {
                _index.SetMeta(id, flags, size, mtime);
            }
        }
    }

    private void ApplyDeleted(string path)
    {
        using (_index.WriteLock())
        {
            int id = _index.Resolve(path);
            if (id >= 0) _index.Remove(id);
        }
    }

    private void ApplyRenamed(string oldPath, string newPath, List<(int id, string path)> newDirs)
    {
        string? newParentPath = Path.GetDirectoryName(newPath);
        if (newParentPath != null && !IsExcluded(newPath))
        {
            ReadOnlySpan<char> name = newPath.AsSpan(newParentPath.Length).TrimStart('\\');
            using (_index.WriteLock())
            {
                int id = _index.Resolve(oldPath);
                int parent = _index.Resolve(newParentPath);
                if (id >= 0 && parent >= 0) _index.Rename(id, parent, name);
                else if (id >= 0) _index.Remove(id);   // moved somewhere we don't track
            }
        }
        else
        {
            ApplyDeleted(oldPath);
        }

        // Unknown source (or it was moved in from elsewhere): treat the destination as new, and refresh metadata otherwise.
        ApplyPresent(newPath, newDirs);
    }

    private bool IsExcluded(string path)
    {
        foreach (string ex in _excludePaths)
            if (path.StartsWith(ex, StringComparison.OrdinalIgnoreCase) &&
                (path.Length == ex.Length || path[ex.Length] == '\\'))
                return true;

        if (_excludeNames.Count == 0) return false;
        ReadOnlySpan<char> rest = path;
        while (!rest.IsEmpty)
        {
            int sep = rest.IndexOf('\\');
            ReadOnlySpan<char> part = sep < 0 ? rest : rest[..sep];
            if (part.Length > 1 && _excludeNames.Contains(part.ToString())) return true;
            if (sep < 0) break;
            rest = rest[(sep + 1)..];
        }
        return false;
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        _events.CompleteAdding();
        _gate.Set();
        _thread?.Join(2000);
    }
}
