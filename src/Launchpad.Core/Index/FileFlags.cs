namespace Launchpad.Core.Index;

[Flags]
public enum EntryFlags : byte
{
    None = 0,
    Directory = 1,
    Hidden = 2,
    System = 4,
    Deleted = 8,
    ReadOnly = 16,
}
