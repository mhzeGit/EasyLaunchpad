namespace Launchpad.Core.Index;

/// <summary>Interns lower-case file extensions into small ids so filters can be array lookups.</summary>
public sealed class ExtensionTable
{
    private readonly List<string> _names = new() { "" };
    private readonly Dictionary<string, ushort> _ids = new(StringComparer.Ordinal) { [""] = 0 };

    public int Count => _names.Count;
    public string this[int id] => _names[id];

    public ushort Intern(string ext)
    {
        if (_ids.TryGetValue(ext, out ushort id)) return id;
        if (_names.Count >= ushort.MaxValue) return 0;
        id = (ushort)_names.Count;
        _names.Add(ext);
        _ids[ext] = id;
        return id;
    }

    public bool TryGet(string ext, out ushort id) => _ids.TryGetValue(ext, out id);

    internal void Write(BinaryWriter w)
    {
        w.Write(_names.Count);
        foreach (var n in _names) w.Write(n);
    }

    internal static ExtensionTable Read(BinaryReader r)
    {
        var t = new ExtensionTable();
        int n = r.ReadInt32();
        if (n < 1 || n > ushort.MaxValue) throw new InvalidDataException("Corrupt extension table");
        t._names.Clear(); t._ids.Clear();
        for (int i = 0; i < n; i++)
        {
            string s = r.ReadString();
            t._names.Add(s);
            t._ids[s] = (ushort)i;
        }
        return t;
    }
}
