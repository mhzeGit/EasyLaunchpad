using System.Diagnostics;
using Launchpad.Core.Index;
using Launchpad.Core.Search;

namespace Launchpad.App.Services;

public enum IndexState { Idle, Loading, Scanning, Ready, Disabled }

/// <summary>
/// Owns the file index for the lifetime of the app: loads the last snapshot instantly, reconciles it with the disk in the
/// background, then keeps it current from file-system notifications and persists it periodically.
/// </summary>
public sealed class IndexService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private CancellationTokenSource _cts = new();
    private IndexWatcher? _watcher;
    private Timer? _saveTimer;
    private long _savedVersion = -1;
    private long _progress;
    private volatile bool _disposed;

    public FileIndex Index { get; private set; } = new();
    public IndexState State { get; private set; } = IndexState.Idle;
    public long ScanProgress => Interlocked.Read(ref _progress);
    public int EntryCount => Index.LiveCount;
    public TimeSpan? LastScanDuration { get; private set; }

    public event Action? StatusChanged;

    public IndexService(AppSettings settings) => _settings = settings;

    // ------------------------------------------------------------------ lifecycle

    public Task StartAsync() => Task.Run(() =>
    {
        if (!_settings.IndexFiles) { SetState(IndexState.Disabled); return; }
        _cts = new CancellationTokenSource();

        bool haveSnapshot = TryLoadSnapshot();
        var options = BuildOptions(background: haveSnapshot);

        _watcher = new IndexWatcher(Index, options);
        _watcher.RescanRequested += _root => { _ = ReconcileSoonAsync(); };
        _watcher.Start();

        RunReconcile(options);
        _watcher.Resume();

        _saveTimer = new Timer(_ => SaveIfChanged(), null, TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(5));
    });

    /// <summary>Turns file indexing off: stops scanning/watching, frees the memory and removes the snapshot.</summary>
    public void Stop()
    {
        _cts.Cancel();
        _saveTimer?.Dispose(); _saveTimer = null;
        _watcher?.Dispose(); _watcher = null;
        _scanGate.Wait();
        try
        {
            Index = new FileIndex(16);
            try { File.Delete(AppPaths.IndexFile); } catch (Exception) { }
        }
        finally { _scanGate.Release(); }
        SetState(IndexState.Disabled);
    }

    /// <summary>Throws the index away and scans everything again.</summary>
    public async Task RebuildAsync()
    {
        if (!_settings.IndexFiles) return;
        await _scanGate.WaitAsync();
        try
        {
            _watcher?.Dispose();
            Index = new FileIndex(1 << 16);
            var options = BuildOptions(background: false);
            _watcher = new IndexWatcher(Index, options);
            _watcher.RescanRequested += _root => { _ = ReconcileSoonAsync(); };
            _watcher.Start();
            await Task.Run(() => RunReconcileCore(options));
            _watcher.Resume();
            Save();
        }
        finally { _scanGate.Release(); }
    }

    public SearchResults Search(Query query, SearchOptions options, CancellationToken ct) =>
        SearchEngine.Run(Index, query, options, ct);

    // ------------------------------------------------------------------ scanning

    private void RunReconcile(ScanOptions options)
    {
        _scanGate.Wait();
        try { RunReconcileCore(options); }
        finally { _scanGate.Release(); }
        Save();
    }

    private void RunReconcileCore(ScanOptions options)
    {
        SetState(IndexState.Scanning);
        Interlocked.Exchange(ref _progress, 0);
        long lastReport = 0;
        var sw = Stopwatch.StartNew();
        var stats = IndexScanner.Reconcile(Index, options, v =>
        {
            Interlocked.Exchange(ref _progress, v);
            if (v - lastReport > 40_000) { lastReport = v; StatusChanged?.Invoke(); }
        }, _cts.Token);
        LastScanDuration = sw.Elapsed;
        // the scan churns through millions of short-lived strings; hand that memory back
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect();
        SetState(stats.Completed ? IndexState.Ready : IndexState.Idle);
    }

    private int _rescanPending;

    /// <summary>The OS dropped notifications; reconcile quietly once things calm down.</summary>
    private async Task ReconcileSoonAsync()
    {
        if (Interlocked.Exchange(ref _rescanPending, 1) == 1) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), _cts.Token);
            Interlocked.Exchange(ref _rescanPending, 0);
            await _scanGate.WaitAsync(_cts.Token);
            try { await Task.Run(() => RunReconcileCore(BuildOptions(background: true))); }
            finally { _scanGate.Release(); }
        }
        catch (OperationCanceledException) { }
        finally { Interlocked.Exchange(ref _rescanPending, 0); }
    }

    private ScanOptions BuildOptions(bool background)
    {
        var roots = _settings.IndexedRoots.Count > 0
            ? _settings.IndexedRoots.ToList()
            : DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName).ToList();

        var excluded = new List<string>(_settings.ExcludedPaths);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        excluded.Add(Path.Combine(windows, "WinSxS"));   // hundreds of thousands of hard links nobody searches for

        return new ScanOptions { Roots = roots, ExcludePaths = excluded, Background = background };
    }

    // ------------------------------------------------------------------ persistence

    /// <summary>Loads the saved snapshot only (no scanning or watching). Used by the off-screen render test.</summary>
    public bool LoadSnapshotOnly() { bool ok = TryLoadSnapshot(); SetState(ok ? IndexState.Ready : IndexState.Idle); return ok; }

    /// <summary>Scans a single folder synchronously (render test only).</summary>
    public void ScanForTest(string root)
    {
        IndexScanner.Reconcile(Index, new ScanOptions { Roots = new[] { root } });
        SetState(IndexState.Ready);
    }

    private bool TryLoadSnapshot()
    {
        SetState(IndexState.Loading);
        try
        {
            if (!File.Exists(AppPaths.IndexFile)) return false;
            using var fs = new FileStream(AppPaths.IndexFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            Index = FileIndex.Load(fs);
            _savedVersion = Index.Version;
            StatusChanged?.Invoke();
            return Index.LiveCount > 0;
        }
        catch (Exception)
        {
            Index = new FileIndex(1 << 16);   // unreadable/old format: rebuild
            return false;
        }
    }

    private void SaveIfChanged()
    {
        if (Index.Version != _savedVersion) Save();
    }

    public void Save()
    {
        try
        {
            long version = Index.Version;
            using (Index.WriteLock()) { if (Index.NeedsCompaction) Index.Compact(); }
            string tmp = AppPaths.IndexFile + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                Index.Save(fs);
            File.Move(tmp, AppPaths.IndexFile, overwrite: true);
            _savedVersion = version;
        }
        catch (Exception) { /* disk full / locked: try again next tick */ }
    }

    private void SetState(IndexState s)
    {
        State = s;
        StatusChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _saveTimer?.Dispose();
        _watcher?.Dispose();
        if (State != IndexState.Disabled && Index.LiveCount > 0) Save();
    }
}
