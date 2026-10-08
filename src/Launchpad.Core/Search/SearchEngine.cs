using System.Diagnostics;
using Launchpad.Core.Index;

namespace Launchpad.Core.Search;

public enum SortKey { Relevance, Name, Path, Size, Modified, Type }

public sealed class SearchOptions
{
    public int Limit { get; init; } = 1000;
    public SortKey Sort { get; init; } = SortKey.Relevance;
    public bool Descending { get; init; }

    /// <summary>Used only by relevance ranking, to prefer the user's own folders over system and cache locations.</summary>
    public string? UserProfile { get; init; } = Environment.GetEnvironmentVariable("USERPROFILE");
}

public sealed record SearchHit(
    string Name, string Path, string Directory, bool IsDirectory, long Size, DateTime Modified, string Extension,
    FileCategory Category, int Score);

public sealed class SearchResults
{
    public static readonly SearchResults Empty = new(new List<SearchHit>(), 0, TimeSpan.Zero, null);

    public SearchResults(List<SearchHit> hits, int total, TimeSpan elapsed, string? error)
    {
        Hits = hits; Total = total; Elapsed = elapsed; Error = error;
    }

    public List<SearchHit> Hits { get; }
    /// <summary>Number of matches in the index (may exceed <see cref="Hits"/>).</summary>
    public int Total { get; }
    public TimeSpan Elapsed { get; }
    public string? Error { get; }
}

/// <summary>
/// Scans the index columns in parallel. Cheap array filters (flags, extension id, size, time) run before any
/// string work, name matching uses vectorised OrdinalIgnoreCase search over the shared name buffer, and
/// full paths are only built for the handful of entries that survive.
/// </summary>
public static class SearchEngine
{
    private readonly record struct Cand(int Id, int Score);

    public static SearchResults Run(FileIndex index, Query q, SearchOptions? options = null, CancellationToken ct = default)
    {
        options ??= new SearchOptions();
        var sw = Stopwatch.StartNew();
        if (q.Error != null) return new SearchResults(new(), 0, sw.Elapsed, q.Error);
        if (q.IsEmpty) return SearchResults.Empty;

        using var _ = index.ReadLock();
        var m = new Matcher(index, q);
        if (m.Impossible) return new SearchResults(new(), 0, sw.Elapsed, null);

        int count = index.Count;
        var all = new List<Cand>();
        const int chunk = 1 << 15;
        int chunks = (count + chunk - 1) / chunk;

        if (count < 40_000)
        {
            for (int id = 0; id < count; id++)
                if (m.Match(id, out int s)) all.Add(new Cand(id, s));
        }
        else
        {
            object gate = new();
            Parallel.For(0, chunks,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8), CancellationToken = ct },
                () => new List<Cand>(),
                (c, _, local) =>
                {
                    int lo = c * chunk, hi = Math.Min(count, lo + chunk);
                    for (int id = lo; id < hi; id++)
                        if (m.Match(id, out int s)) local.Add(new Cand(id, s));
                    return local;
                },
                local => { lock (gate) all.AddRange(local); });
        }
        ct.ThrowIfCancellationRequested();

        int total = all.Count;
        bool relevance = options.Sort == SortKey.Relevance;
        bool noTerms = !q.HasPositiveTerms;
        int limit = Math.Max(1, options.Limit);

        if (relevance && noTerms)
        {
            // Nothing to rank on: show the most recently modified first.
            options = new SearchOptions { Limit = options.Limit, Sort = SortKey.Modified, Descending = true, UserProfile = options.UserProfile };
            relevance = false;
        }

        var cmp = BuildComparison(index, options, relevance);
        var top = TopK(all, relevance ? Math.Min(total, limit * 3) : limit, cmp);

        if (relevance)
        {
            for (int i = 0; i < top.Count; i++)
            {
                string path = index.GetPath(top[i].Id);
                top[i] = top[i] with { Score = top[i].Score + PathBonus(path, options.UserProfile) };
            }
            top.Sort(cmp);
            if (top.Count > limit) top.RemoveRange(limit, top.Count - limit);
        }

        var hits = new List<SearchHit>(top.Count);
        foreach (var c in top) hits.Add(ToHit(index, c));
        return new SearchResults(hits, total, sw.Elapsed, null);
    }

    // ------------------------------------------------------------------ ordering

    private static Comparison<Cand> BuildComparison(FileIndex ix, SearchOptions o, bool relevance)
    {
        int dir = o.Descending ? -1 : 1;
        static int ByName(FileIndex ix, int a, int b) =>
            ix.NameSpan(a).CompareTo(ix.NameSpan(b), StringComparison.OrdinalIgnoreCase);

        if (relevance)
        {
            // Highest score first. `Descending` flips to lowest-first, which nobody wants but is consistent.
            return (a, b) =>
            {
                int c = b.Score.CompareTo(a.Score);
                if (c == 0) c = ix.Mtime[b.Id].CompareTo(ix.Mtime[a.Id]);
                if (c == 0) c = a.Id.CompareTo(b.Id);
                return c * dir;
            };
        }

        switch (o.Sort)
        {
            case SortKey.Name:
                return (a, b) => { int c = ByName(ix, a.Id, b.Id); return (c != 0 ? c : a.Id.CompareTo(b.Id)) * dir; };
            case SortKey.Size:
                return (a, b) => { int c = ix.Size[a.Id].CompareTo(ix.Size[b.Id]); return (c != 0 ? c : ByName(ix, a.Id, b.Id)) * dir; };
            case SortKey.Modified:
                return (a, b) => { int c = ix.Mtime[a.Id].CompareTo(ix.Mtime[b.Id]); return (c != 0 ? c : ByName(ix, a.Id, b.Id)) * dir; };
            case SortKey.Type:
                return (a, b) =>
                {
                    int c = string.CompareOrdinal(ix.Extensions[ix.Ext[a.Id]], ix.Extensions[ix.Ext[b.Id]]);
                    return (c != 0 ? c : ByName(ix, a.Id, b.Id)) * dir;
                };
            default: // Path: group by containing folder, then name
                var dirCache = new Dictionary<int, string>();
                string DirOf(int id)
                {
                    int p = ix.Parent[id];
                    if (p < 0) return "";
                    if (!dirCache.TryGetValue(p, out string? s)) dirCache[p] = s = ix.GetPath(p);
                    return s;
                }
                return (a, b) =>
                {
                    int c = string.Compare(DirOf(a.Id), DirOf(b.Id), StringComparison.OrdinalIgnoreCase);
                    return (c != 0 ? c : ByName(ix, a.Id, b.Id)) * dir;
                };
        }
    }

    /// <summary>The best <paramref name="k"/> candidates in order, without sorting everything when there are millions.</summary>
    private static List<Cand> TopK(List<Cand> all, int k, Comparison<Cand> cmp)
    {
        if (all.Count <= 100_000)
        {
            all.Sort(cmp);
            if (all.Count > k) all.RemoveRange(k, all.Count - k);
            return all;
        }

        var heap = new PriorityQueue<Cand, Cand>(k + 1, Comparer<Cand>.Create((a, b) => cmp(b, a)));
        foreach (var c in all)
        {
            if (heap.Count < k) heap.Enqueue(c, c);
            else if (heap.TryPeek(out var worst, out _) && cmp(c, worst) < 0) heap.EnqueueDequeue(c, c);
        }
        var result = new List<Cand>(heap.Count);
        while (heap.TryDequeue(out var c, out _)) result.Add(c);
        result.Sort(cmp);
        return result;
    }

    private static int PathBonus(string path, string? profile)
    {
        int bonus = 0;
        if (profile != null && path.StartsWith(profile, StringComparison.OrdinalIgnoreCase))
        {
            bonus += 80;
            ReadOnlySpan<char> rest = path.AsSpan(profile.Length).TrimStart('\\');
            foreach (string good in UserFolders)
                if (rest.StartsWith(good, StringComparison.OrdinalIgnoreCase)) { bonus += 60; break; }
        }
        // Generated / system locations should sink below any reasonable match in the user's own files.
        foreach (string marker in GeneratedMarkers)
            if (path.Contains(marker, StringComparison.OrdinalIgnoreCase)) { bonus -= 450; break; }
        if (HasDotFolder(path)) bonus -= 150;
        if (path.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase)) bonus -= 250;
        if (path.StartsWith(@"C:\Windows", StringComparison.OrdinalIgnoreCase)) bonus -= 300;
        if (path.Contains(@"\$", StringComparison.Ordinal)) bonus -= 200;
        if (path.StartsWith(@"C:\Program Files", StringComparison.OrdinalIgnoreCase)) bonus -= 60;

        int depth = 0;
        foreach (char ch in path) if (ch == '\\') depth++;
        return bonus - depth * 4;
    }

    /// <summary>Folders full of machine-made files that almost never are what the user is looking for.</summary>
    private static readonly string[] GeneratedMarkers =
    {
        @"\node_modules\", @"\site-packages\", @"\.venv\", @"\venv\", @"\__pycache__\", @"\PackageCache\",
        @"\.git\", @"\.gradle\", @"\.nuget\", @"\.cache\", @"\obj\Debug\", @"\obj\Release\", @"\.vs\", @"\target\debug\",
    };

    /// <summary>True if any folder (not the file name itself) in the path starts with a dot: hidden config/tool directories.</summary>
    private static bool HasDotFolder(string path)
    {
        int i = path.IndexOf(@"\.", StringComparison.Ordinal);
        while (i >= 0)
        {
            if (path.IndexOf('\\', i + 2) > 0) return true;
            i = path.IndexOf(@"\.", i + 2, StringComparison.Ordinal);
        }
        return false;
    }

    private static readonly string[] UserFolders = { "Desktop", "Documents", "Downloads", "Pictures", "Music", "Videos", "OneDrive" };

    private static SearchHit ToHit(FileIndex ix, Cand c)
    {
        int id = c.Id;
        string name = ix.GetName(id);
        string path = ix.GetPath(id);
        int p = ix.Parent[id];
        string dir = p < 0 ? "" : ix.GetPath(p);
        bool isDir = ix.IsDirectory(id);
        string ext = isDir ? "" : ix.Extensions[ix.Ext[id]];
        long mt = ix.Mtime[id];
        DateTime modified = DateTime.MinValue;
        try { if (mt > 0) modified = DateTime.FromFileTime(mt); } catch (ArgumentOutOfRangeException) { }
        return new SearchHit(name, path, dir, isDir, ix.Size[id], modified, ext,
            isDir ? FileCategory.Other : FileTypes.Categorize(ext), c.Score);
    }

    // ------------------------------------------------------------------ matching

    private sealed class Matcher
    {
        private readonly FileIndex _ix;
        private readonly Query _q;
        private readonly string[] _pos, _neg;
        private readonly Term[] _wildPos, _wildNeg, _regexPos, _regexNeg, _pathPos, _pathNeg;
        private readonly bool[]? _extOk;
        private readonly bool _positiveExtFilter;
        private readonly int _ancestor;
        private readonly bool _needsPath;
        public readonly bool Impossible;

        public Matcher(FileIndex ix, Query q)
        {
            _ix = ix; _q = q;
            _pos = q.Terms.Where(t => t.Mode == TermMode.Substring && !t.Negate).Select(t => t.Text).ToArray();
            _neg = q.Terms.Where(t => t.Mode == TermMode.Substring && t.Negate).Select(t => t.Text).ToArray();
            _wildPos = q.Terms.Where(t => t.Mode == TermMode.Wildcard && !t.Negate).ToArray();
            _wildNeg = q.Terms.Where(t => t.Mode == TermMode.Wildcard && t.Negate).ToArray();
            _regexPos = q.Terms.Where(t => t.Mode == TermMode.Regex && !t.Negate).ToArray();
            _regexNeg = q.Terms.Where(t => t.Mode == TermMode.Regex && t.Negate).ToArray();
            _pathPos = q.Terms.Where(t => t.Mode == TermMode.Path && !t.Negate).ToArray();
            _pathNeg = q.Terms.Where(t => t.Mode == TermMode.Path && t.Negate).ToArray();
            _needsPath = _pathPos.Length + _pathNeg.Length > 0;

            _positiveExtFilter = q.Extensions.Count > 0 || q.Categories.Count > 0;
            if (_positiveExtFilter || q.ExcludedExtensions.Count > 0)
            {
                var ok = new bool[ix.Extensions.Count];
                for (int i = 0; i < ok.Length; i++)
                {
                    string e = ix.Extensions[i];
                    bool allowed = !_positiveExtFilter
                                   || q.Extensions.Contains(e)
                                   || (q.Categories.Count > 0 && q.Categories.Contains(FileTypes.Categorize(e)));
                    if (allowed && q.ExcludedExtensions.Contains(e)) allowed = false;
                    ok[i] = allowed;
                }
                _extOk = ok;
            }

            _ancestor = -1;
            if (q.InPath != null)
            {
                _ancestor = ix.Resolve(q.InPath);
                if (_ancestor < 0) Impossible = true;
            }
            if (q.FoldersOnly && q.FilesOnly) Impossible = true;
            if (q.FoldersOnly && _positiveExtFilter) Impossible = true;
        }

        public bool Match(int id, out int score)
        {
            score = 0;
            var ix = _ix;
            var flags = ix.Flags[id];
            if ((flags & EntryFlags.Deleted) != 0) return false;

            bool isDir = (flags & EntryFlags.Directory) != 0;
            if (isDir ? _q.FilesOnly : _q.FoldersOnly) return false;
            if (!_q.IncludeHidden && (flags & (EntryFlags.Hidden | EntryFlags.System)) != 0) return false;

            if (_extOk != null)
            {
                if (isDir) { if (_positiveExtFilter) return false; }
                else if (!_extOk[ix.Ext[id]]) return false;
            }
            if (_q.MinSize != null || _q.MaxSize != null)
            {
                if (isDir) return false;
                long sz = ix.Size[id];
                if (sz < (_q.MinSize ?? 0) || sz > (_q.MaxSize ?? long.MaxValue)) return false;
            }
            if (_q.MinTime != null || _q.MaxTime != null)
            {
                long t = ix.Mtime[id];
                if (t < (_q.MinTime ?? 0) || t > (_q.MaxTime ?? long.MaxValue)) return false;
            }

            ReadOnlySpan<char> name = ix.NameSpan(id);
            int s = 0;
            foreach (string term in _pos)
            {
                int pos = name.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                if (pos < 0) return false;
                s += TermScore(name, pos, term.Length, isDir);
            }
            foreach (string term in _neg)
                if (name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) return false;
            foreach (var t in _wildPos) { if (!Glob(t.Text, name)) return false; s += 500; }
            foreach (var t in _wildNeg) if (Glob(t.Text, name)) return false;
            foreach (var t in _regexPos) { if (!SafeRegex(t, name)) return false; s += 300; }
            foreach (var t in _regexNeg) if (SafeRegex(t, name)) return false;

            if (_needsPath)
            {
                string path = ix.GetPath(id);
                foreach (var t in _pathPos)
                {
                    if (path.IndexOf(t.Text, StringComparison.OrdinalIgnoreCase) < 0) return false;
                    s += 200;
                }
                foreach (var t in _pathNeg)
                    if (path.IndexOf(t.Text, StringComparison.OrdinalIgnoreCase) >= 0) return false;
            }

            if (!ChainOk(id)) return false;

            if ((flags & (EntryFlags.Hidden | EntryFlags.System)) != 0) s -= 150;
            score = s - Math.Min(name.Length, 200);
            return true;
        }

        private bool ChainOk(int id)
        {
            int cur = _ix.Parent[id];
            bool foundAncestor = _ancestor < 0;
            for (int depth = 0; cur >= 0; depth++)
            {
                if (depth > 512) return false;
                if ((_ix.Flags[cur] & EntryFlags.Deleted) != 0) return false;
                if (cur == _ancestor) foundAncestor = true;
                cur = _ix.Parent[cur];
            }
            return foundAncestor;
        }

        private static bool SafeRegex(Term t, ReadOnlySpan<char> name)
        {
            try { return t.Regex!.IsMatch(name); }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { return false; }
        }

        private static int TermScore(ReadOnlySpan<char> name, int pos, int len, bool isDir)
        {
            int dot = isDir ? -1 : name.LastIndexOf('.');
            int stem = dot > 0 ? dot : name.Length;
            if (pos == 0 && len == stem) return 1000;
            if (pos == 0) return 600;
            char prev = name[pos - 1];
            if (prev is ' ' or '_' or '-' or '.' or '(' or '[' or '+') return 350;
            if (char.IsLower(prev) && char.IsUpper(name[pos])) return 300;   // camelCase boundary
            return 100;
        }
    }

    /// <summary>Case-insensitive glob over the whole name: <c>*</c> any run, <c>?</c> any one char.</summary>
    internal static bool Glob(ReadOnlySpan<char> p, ReadOnlySpan<char> t)
    {
        int pi = 0, ti = 0, star = -1, mark = 0;
        while (ti < t.Length)
        {
            if (pi < p.Length && (p[pi] == '?' || char.ToUpperInvariant(p[pi]) == char.ToUpperInvariant(t[ti]))) { pi++; ti++; }
            else if (pi < p.Length && p[pi] == '*') { star = pi++; mark = ti; }
            else if (star >= 0) { pi = star + 1; ti = ++mark; }
            else return false;
        }
        while (pi < p.Length && p[pi] == '*') pi++;
        return pi == p.Length;
    }
}
