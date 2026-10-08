using System.Runtime.InteropServices;

namespace Launchpad.Core.Index;

internal struct DirEntry
{
    public int Off;
    public int Len;
    public uint Attr;
    public long Size;
    public long Mtime;

    public readonly bool IsDirectory => (Attr & Win32Find.AttrDirectory) != 0;
    public readonly bool IsReparse => (Attr & Win32Find.AttrReparse) != 0;

    public readonly EntryFlags ToFlags()
    {
        var f = EntryFlags.None;
        if ((Attr & Win32Find.AttrDirectory) != 0) f |= EntryFlags.Directory;
        if ((Attr & Win32Find.AttrHidden) != 0) f |= EntryFlags.Hidden;
        if ((Attr & Win32Find.AttrSystem) != 0) f |= EntryFlags.System;
        if ((Attr & Win32Find.AttrReadOnly) != 0) f |= EntryFlags.ReadOnly;
        return f;
    }
}

/// <summary>Reusable buffer holding one directory listing without per-entry string allocations.</summary>
internal sealed class DirBuffer
{
    public char[] Chars = new char[16 * 1024];
    public int CharsLen;
    public DirEntry[] Items = new DirEntry[512];
    public int Count;

    public void Reset() { CharsLen = 0; Count = 0; }

    public ReadOnlySpan<char> Name(int i) => new(Chars, Items[i].Off, Items[i].Len);

    public unsafe void Add(char* name, int len, uint attr, long size, long mtime)
    {
        if (Count == Items.Length) Array.Resize(ref Items, Items.Length * 2);
        if (CharsLen + len > Chars.Length) Array.Resize(ref Chars, Math.Max(Chars.Length * 2, CharsLen + len));
        new ReadOnlySpan<char>(name, len).CopyTo(Chars.AsSpan(CharsLen));
        Items[Count++] = new DirEntry { Off = CharsLen, Len = len, Attr = attr, Size = size, Mtime = mtime };
        CharsLen += len;
    }
}

/// <summary>Thin FindFirstFileEx wrapper tuned for bulk enumeration (no short names, large fetch).</summary>
internal static unsafe class Win32Find
{
    public const uint AttrReadOnly = 0x1, AttrHidden = 0x2, AttrSystem = 0x4, AttrDirectory = 0x10, AttrReparse = 0x400;

    // Pack = 4 matches the C layout, where FILETIME is only 4-byte aligned.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct FindData
    {
        public uint Attributes;
        public long CreationTime, AccessTime, WriteTime;   // FILETIME == two uints == one long on little-endian
        public uint SizeHigh, SizeLow;
        public uint Reserved0, Reserved1;
        public fixed char FileName[260];
        public fixed char Alternate[14];
    }

    [DllImport("kernel32.dll", EntryPoint = "FindFirstFileExW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr FindFirstFileEx(string fileName, int infoLevel, FindData* data, int searchOp, IntPtr filter, int flags);

    [DllImport("kernel32.dll", EntryPoint = "FindNextFileW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool FindNextFile(IntPtr handle, FindData* data);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern bool FindClose(IntPtr handle);

    private static readonly IntPtr InvalidHandle = new(-1);
    private const int FindExInfoBasic = 1;
    private const int FindExSearchNameMatch = 0;
    private const int FindFirstExLargeFetch = 2;

    /// <summary>Lists a directory into <paramref name="buf"/>. Returns false if it can't be opened (access denied, vanished, ...).</summary>
    public static bool Enumerate(string dir, DirBuffer buf)
    {
        buf.Reset();
        string pattern = LongPrefix(dir) + (dir.EndsWith('\\') ? "*" : "\\*");
        FindData d;
        IntPtr h = FindFirstFileEx(pattern, FindExInfoBasic, &d, FindExSearchNameMatch, IntPtr.Zero, FindFirstExLargeFetch);
        if (h == InvalidHandle) return false;
        try
        {
            do { Append(&d, buf); }
            while (FindNextFile(h, &d));
        }
        finally { FindClose(h); }
        return true;
    }

    /// <summary>Stats one path. Returns false if it doesn't exist.</summary>
    public static bool TryStat(string path, out uint attr, out long size, out long mtime)
    {
        attr = 0; size = 0; mtime = 0;
        if (path.Length < 4 || path.EndsWith('\\')) return false;
        FindData d;
        IntPtr h = FindFirstFileEx(LongPrefix(path), FindExInfoBasic, &d, FindExSearchNameMatch, IntPtr.Zero, 0);
        if (h == InvalidHandle) return false;
        FindClose(h);
        attr = d.Attributes;
        size = (attr & AttrDirectory) != 0 ? 0 : ((long)d.SizeHigh << 32) | d.SizeLow;
        mtime = d.WriteTime;
        return true;
    }

    private static void Append(FindData* d, DirBuffer buf)
    {
        char* name = d->FileName;
        int len = 0;
        while (len < 260 && name[len] != '\0') len++;
        if (name[0] == '.' && (len == 1 || (len == 2 && name[1] == '.'))) return;
        uint attr = d->Attributes;
        long size = (attr & AttrDirectory) != 0 ? 0 : ((long)d->SizeHigh << 32) | d->SizeLow;
        buf.Add(name, len, attr, size, d->WriteTime);
    }

    public static string LongPrefix(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path[2..];
        return @"\\?\" + path;
    }
}
