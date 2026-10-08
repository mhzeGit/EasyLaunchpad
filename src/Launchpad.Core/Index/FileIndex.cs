using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Launchpad.Core.Index;

/// <summary>
/// In-memory file catalogue stored as a struct-of-arrays. Names live in one contiguous UTF-16
/// buffer, parents are referenced by id (so renaming a folder is O(1)), and a compact open-addressing
/// hash table maps (parent, name) to an id so file-system events can be applied without scanning.
///
/// Thread model: callers take <see cref="ReadLock"/> / <see cref="WriteLock"/> explicitly. Everything
/// marked "caller holds lock" must be used inside one of those scopes.
/// </summary>
public sealed class FileIndex
{
    public const int NoParent = -1;
    private const int MaxDepth = 512;

    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    // ---- columns (all indexed by entry id) ----
    internal char[] Chars;
    internal int CharsLen;
    internal int[] NameOff;
    internal ushort[] NameLen;
    internal int[] Parent;
    internal long[] Size;
    internal long[] Mtime;          // FILETIME (100ns ticks since 1601)
    internal EntryFlags[] Flags;
    internal ushort[] Ext;
    internal int Count;             // ids [0, Count) have been handed out

    private int _deadCount;
    private long _garbageChars;

    // ---- (parent, name) -> id lookup ----
    private int[] _table = null!;           // 0 = empty, -1 = tombstone, otherwise id + 1
    private int _tableMask;
    private int _tableUsed;         // live + tombstones

    public ExtensionTable Extensions { get; private set; } = new();

    /// <summary>Raised (without any lock held) is not guaranteed; used only for coarse "something changed" hints.</summary>
    public long Version;

    public FileIndex(int initialCapacity = 1024)
    {
        initialCapacity = Math.Max(16, initialCapacity);
        Chars = new char[initialCapacity * 16];
        NameOff = new int[initialCapacity];
        NameLen = new ushort[initialCapacity];
        Parent = new int[initialCapacity];
        Size = new long[initialCapacity];
        Mtime = new long[initialCapacity];
        Flags = new EntryFlags[initialCapacity];
        Ext = new ushort[initialCapacity];
        InitTable(initialCapacity * 2);
    }

    // ------------------------------------------------------------------ locking

    public LockScope ReadLock() { _lock.EnterReadLock(); return new LockScope(_lock, write: false); }
    public LockScope WriteLock() { _lock.EnterWriteLock(); return new LockScope(_lock, write: true); }

    public readonly struct LockScope : IDisposable
    {
        private readonly ReaderWriterLockSlim _l;
        private readonly bool _write;
        internal LockScope(ReaderWriterLockSlim l, bool write) { _l = l; _write = write; }
        public void Dispose() { if (_write) _l.ExitWriteLock(); else _l.ExitReadLock(); }
    }

    // ------------------------------------------------------------------ stats (caller holds lock, or approximate)

    public int LiveCount => Count - _deadCount;
    public int DeadCount => _deadCount;
    public int TotalSlots => Count;

    // ------------------------------------------------------------------ mutation (caller holds write lock)

    /// <summary>Adds a new entry. The caller must have verified that (parent, name) does not already exist.</summary>
    public int Add(int parent, ReadOnlySpan<char> name, EntryFlags flags, long size, long mtime)
    {
        if (Count == NameOff.Length) GrowColumns();
        int id = Count++;
        NameOff[id] = AppendName(name);
        NameLen[id] = (ushort)Math.Min(name.Length, ushort.MaxValue);
        Parent[id] = parent;
        Size[id] = size;
        Mtime[id] = mtime;
        Flags[id] = flags & ~EntryFlags.Deleted;
        Ext[id] = (flags & EntryFlags.Directory) != 0 ? (ushort)0 : Extensions.Intern(ExtensionOf(name));
        TableInsert(id);
        Version++;
        return id;
    }

    /// <summary>Finds the live entry (parent, name) or returns -1.</summary>
    public int Find(int parent, ReadOnlySpan<char> name)
    {
        uint h = Hash(parent, name);
        int mask = _tableMask;
        int[] table = _table;
        for (int i = (int)(h & (uint)mask); ; i = (i + 1) & mask)
        {
            int v = table[i];
            if (v == 0) return -1;
            if (v > 0)
            {
                int id = v - 1;
                if (Parent[id] == parent && NameSpan(id).Equals(name, StringComparison.OrdinalIgnoreCase))
                    return id;
            }
        }
    }

    /// <summary>Adds or refreshes (parent, name). Returns the id; <paramref name="added"/> tells which happened.</summary>
    public int Upsert(int parent, ReadOnlySpan<char> name, EntryFlags flags, long size, long mtime, out bool added)
    {
        int id = Find(parent, name);
        if (id < 0)
        {
            added = true;
            return Add(parent, name, flags, size, mtime);
        }
        added = false;
        SetMeta(id, flags, size, mtime);
        return id;
    }

    /// <summary>Updates size/mtime/flags. Returns true if anything changed.</summary>
    public bool SetMeta(int id, EntryFlags flags, long size, long mtime)
    {
        flags &= ~EntryFlags.Deleted;
        bool changed = Size[id] != size || Mtime[id] != mtime || Flags[id] != flags;
        if (changed)
        {
            Size[id] = size; Mtime[id] = mtime; Flags[id] = flags;
            Version++;
        }
        return changed;
    }

    /// <summary>Marks an entry as deleted. Descendants become unreachable and are dropped by compaction.</summary>
    public void Remove(int id)
    {
        if ((Flags[id] & EntryFlags.Deleted) != 0) return;
        TableRemove(id);
        Flags[id] |= EntryFlags.Deleted;
        _deadCount++;
        _garbageChars += NameLen[id];
        Version++;
    }

    /// <summary>Renames and/or moves an entry. Children follow automatically because they reference the id.</summary>
    public void Rename(int id, int newParent, ReadOnlySpan<char> newName)
    {
        int clash = Find(newParent, newName);
        if (clash >= 0 && clash != id) Remove(clash);

        TableRemove(id);
        _garbageChars += NameLen[id];
        NameOff[id] = AppendName(newName);
        NameLen[id] = (ushort)Math.Min(newName.Length, ushort.MaxValue);
        Parent[id] = newParent;
        if ((Flags[id] & EntryFlags.Directory) == 0)
            Ext[id] = Extensions.Intern(ExtensionOf(newName));
        TableInsert(id);
        Version++;
    }

    // ------------------------------------------------------------------ queries (caller holds read or write lock)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<char> NameSpan(int id) => new(Chars, NameOff[id], NameLen[id]);

    public string GetName(int id) => new(NameSpan(id));
    public bool IsDirectory(int id) => (Flags[id] & EntryFlags.Directory) != 0;
    public bool IsDeleted(int id) => (Flags[id] & EntryFlags.Deleted) != 0;

    /// <summary>True if the entry and every ancestor is still alive.</summary>
    public bool IsLive(int id)
    {
        for (int depth = 0; id >= 0 && depth < MaxDepth; depth++)
        {
            if ((Flags[id] & EntryFlags.Deleted) != 0) return false;
            id = Parent[id];
        }
        return id < 0;
    }

    public string GetPath(int id)
    {
        Span<int> chain = stackalloc int[MaxDepth];
        int n = 0, total = 0;
        for (int cur = id; cur >= 0 && n < MaxDepth; cur = Parent[cur])
        {
            chain[n++] = cur;
            total += NameLen[cur] + 1;
        }
        if (n == 1) // a root: "C:" -> "C:\"
            return string.Concat(NameSpan(chain[0]), "\\");

        var sb = new StringBuilder(total);
        for (int i = n - 1; i >= 0; i--)
        {
            sb.Append(NameSpan(chain[i]));
            if (i > 0) sb.Append('\\');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Makes sure every folder along <paramref name="path"/> exists (creating missing ones) and returns the last id.
    /// Lets a scan root be any folder, not just a drive. Existing/created ids are appended to <paramref name="chain"/>.
    /// </summary>
    public int EnsurePath(string path, List<int>? chain = null)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        int cur = NoParent;
        foreach (string part in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            int id = Find(cur, part);
            if (id < 0) id = Add(cur, part, EntryFlags.Directory, 0, 0);
            chain?.Add(id);
            cur = id;
        }
        return cur;
    }

    /// <summary>Resolves a full path to a live entry id, or -1.</summary>
    public int Resolve(string path)
    {
        if (string.IsNullOrEmpty(path)) return -1;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        ReadOnlySpan<char> rest = path.AsSpan().TrimEnd('\\');
        if (rest.IsEmpty) return -1;

        int cur = NoParent;
        while (!rest.IsEmpty)
        {
            int sep = rest.IndexOf('\\');
            ReadOnlySpan<char> part = sep < 0 ? rest : rest[..sep];
            rest = sep < 0 ? default : rest[(sep + 1)..];
            if (part.IsEmpty) continue;
            cur = Find(cur, part);
            if (cur < 0) return -1;
        }
        return cur;
    }

    public IEnumerable<int> LiveIds()
    {
        for (int i = 0; i < Count; i++)
            if ((Flags[i] & EntryFlags.Deleted) == 0) yield return i;
    }

    // ------------------------------------------------------------------ compaction (caller holds write lock)

    /// <summary>True when enough garbage has accumulated that rebuilding the columns pays off.</summary>
    public bool NeedsCompaction =>
        (_deadCount > 50_000 && _deadCount > Count / 5) || (_garbageChars > CharsLen / 3 && CharsLen > 4_000_000);

    /// <summary>Drops deleted entries and orphans (children of deleted folders), re-packs names, and rebuilds the lookup table.</summary>
    public int Compact()
    {
        int n = Count;
        // 0 = unknown, 1 = alive, 2 = dead
        var state = new byte[n];
        var stack = new int[MaxDepth + 1];
        for (int i = 0; i < n; i++)
        {
            if (state[i] != 0) continue;
            int sp = 0, cur = i, verdict = 0;
            while (true)
            {
                if (cur < 0) { verdict = 1; break; }
                byte s = state[cur];
                if (s != 0) { verdict = s; break; }
                if ((Flags[cur] & EntryFlags.Deleted) != 0) { state[cur] = 2; verdict = 2; break; }
                if (sp >= MaxDepth) { verdict = 2; break; }
                stack[sp++] = cur;
                cur = Parent[cur];
            }
            while (sp > 0) state[stack[--sp]] = (byte)verdict;
        }

        var map = new int[n];
        int live = 0;
        for (int i = 0; i < n; i++) map[i] = state[i] == 1 ? live++ : -1;
        int removed = n - live;
        if (removed == 0 && _garbageChars == 0) return 0;

        long charsNeeded = 0;
        for (int i = 0; i < n; i++) if (map[i] >= 0) charsNeeded += NameLen[i];

        int cap = Math.Max(1024, live + live / 8);
        var chars = new char[Math.Max(1024, (int)charsNeeded + (int)(charsNeeded / 8))];
        var nameOff = new int[cap]; var nameLen = new ushort[cap]; var parent = new int[cap];
        var size = new long[cap]; var mtime = new long[cap]; var flags = new EntryFlags[cap]; var ext = new ushort[cap];

        int pos = 0;
        for (int i = 0; i < n; i++)
        {
            int j = map[i];
            if (j < 0) continue;
            int len = NameLen[i];
            Array.Copy(Chars, NameOff[i], chars, pos, len);
            nameOff[j] = pos; nameLen[j] = (ushort)len; pos += len;
            int p = Parent[i];
            parent[j] = p < 0 ? NoParent : map[p];
            size[j] = Size[i]; mtime[j] = Mtime[i]; flags[j] = Flags[i]; ext[j] = Ext[i];
        }

        Chars = chars; CharsLen = pos; NameOff = nameOff; NameLen = nameLen; Parent = parent;
        Size = size; Mtime = mtime; Flags = flags; Ext = ext;
        Count = live; _deadCount = 0; _garbageChars = 0;
        RebuildTable();
        Version++;
        return removed;
    }

    // ------------------------------------------------------------------ persistence

    private const int Magic = 0x5844504C; // "LPDX"
    private const int FormatVersion = 3;

    /// <summary>Writes a snapshot. Takes the read lock for the duration (writers briefly wait).</summary>
    public void Save(Stream s)
    {
        using var _ = ReadLock();
        using var w = new BinaryWriter(s, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic); w.Write(FormatVersion);
        w.Write(Count); w.Write(CharsLen); w.Write(_deadCount); w.Write(_garbageChars);
        Extensions.Write(w);
        w.Flush();
        WriteArray(s, Chars.AsSpan(0, CharsLen));
        WriteArray(s, NameOff.AsSpan(0, Count));
        WriteArray(s, NameLen.AsSpan(0, Count));
        WriteArray(s, Parent.AsSpan(0, Count));
        WriteArray(s, Size.AsSpan(0, Count));
        WriteArray(s, Mtime.AsSpan(0, Count));
        WriteArray(s, Flags.AsSpan(0, Count));
        WriteArray(s, Ext.AsSpan(0, Count));
    }

    public static FileIndex Load(Stream s)
    {
        using var r = new BinaryReader(s, Encoding.UTF8, leaveOpen: true);
        if (r.ReadInt32() != Magic) throw new InvalidDataException("Not an index snapshot");
        if (r.ReadInt32() != FormatVersion) throw new InvalidDataException("Unsupported snapshot version");
        int count = r.ReadInt32(), charsLen = r.ReadInt32(), dead = r.ReadInt32();
        long garbage = r.ReadInt64();
        if (count < 0 || charsLen < 0 || dead < 0 || dead > count) throw new InvalidDataException("Corrupt header");

        var idx = new FileIndex(Math.Max(16, count + count / 16));
        idx.Extensions = ExtensionTable.Read(r);
        idx.Chars = new char[Math.Max(1024, charsLen + charsLen / 16)];
        ReadArray(s, idx.Chars.AsSpan(0, charsLen));
        ReadArray(s, idx.NameOff.AsSpan(0, count));
        ReadArray(s, idx.NameLen.AsSpan(0, count));
        ReadArray(s, idx.Parent.AsSpan(0, count));
        ReadArray(s, idx.Size.AsSpan(0, count));
        ReadArray(s, idx.Mtime.AsSpan(0, count));
        ReadArray(s, idx.Flags.AsSpan(0, count));
        ReadArray(s, idx.Ext.AsSpan(0, count));
        idx.Count = count; idx.CharsLen = charsLen; idx._deadCount = dead; idx._garbageChars = garbage;
        idx.RebuildTable();
        return idx;
    }

    private static void WriteArray<T>(Stream s, Span<T> data) where T : unmanaged
        => s.Write(MemoryMarshal.AsBytes((ReadOnlySpan<T>)data));

    private static void ReadArray<T>(Stream s, Span<T> dest) where T : unmanaged
        => s.ReadExactly(MemoryMarshal.AsBytes(dest));

    // ------------------------------------------------------------------ internals

    private int AppendName(ReadOnlySpan<char> name)
    {
        int len = Math.Min(name.Length, ushort.MaxValue);
        if (CharsLen + len > Chars.Length)
        {
            long want = Math.Max((long)Chars.Length * 3 / 2, (long)CharsLen + len + 1024);
            Array.Resize(ref Chars, (int)Math.Min(want, Array.MaxLength));
        }
        name[..len].CopyTo(Chars.AsSpan(CharsLen));
        int off = CharsLen;
        CharsLen += len;
        return off;
    }

    private void GrowColumns()
    {
        int cap = (int)Math.Min((long)NameOff.Length * 3 / 2 + 16, Array.MaxLength);
        Array.Resize(ref NameOff, cap); Array.Resize(ref NameLen, cap); Array.Resize(ref Parent, cap);
        Array.Resize(ref Size, cap); Array.Resize(ref Mtime, cap); Array.Resize(ref Flags, cap); Array.Resize(ref Ext, cap);
    }

    internal static string ExtensionOf(ReadOnlySpan<char> name)
    {
        int dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1) return "";
        ReadOnlySpan<char> e = name[(dot + 1)..];
        if (e.Length > 16) return "";
        return string.Create(e.Length, e.ToString(), static (dst, src) =>
        {
            for (int i = 0; i < dst.Length; i++) dst[i] = char.ToLowerInvariant(src[i]);
        });
    }

    private void InitTable(int minSlots)
    {
        int cap = 16;
        while (cap < minSlots) cap <<= 1;
        _table = new int[cap];
        _tableMask = cap - 1;
        _tableUsed = 0;
    }

    private void RebuildTable()
    {
        InitTable((Count - _deadCount) * 2 + 16);
        for (int id = 0; id < Count; id++)
            if ((Flags[id] & EntryFlags.Deleted) == 0) RawInsert(id);
    }

    private void TableInsert(int id)
    {
        if ((_tableUsed + 1) * 10 > _table.Length * 6) RebuildTable();
        else RawInsert(id);
    }

    private void RawInsert(int id)
    {
        uint h = Hash(Parent[id], NameSpan(id));
        int mask = _tableMask;
        int i = (int)(h & (uint)mask);
        while (_table[i] > 0) i = (i + 1) & mask;
        if (_table[i] == 0) _tableUsed++;
        _table[i] = id + 1;
    }

    private void TableRemove(int id)
    {
        uint h = Hash(Parent[id], NameSpan(id));
        int mask = _tableMask;
        for (int i = (int)(h & (uint)mask); ; i = (i + 1) & mask)
        {
            int v = _table[i];
            if (v == 0) return;
            if (v == id + 1) { _table[i] = -1; return; }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Hash(int parent, ReadOnlySpan<char> name)
    {
        uint h = (uint)parent * 0x9E3779B1u + 0x85EBCA6Bu;
        foreach (char c in name)
        {
            char u = c < 128 ? (c >= 'a' && c <= 'z' ? (char)(c - 32) : c) : char.ToUpperInvariant(c);
            h = (h ^ u) * 16777619u;
        }
        h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12;
        return h;
    }
}
