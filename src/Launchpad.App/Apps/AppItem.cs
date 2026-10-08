using System.Windows.Media;

namespace Launchpad.App.Apps;

public sealed class AppItem
{
    public AppItem(string id, string name, string target, bool isUtility = false, bool isCustom = false)
    {
        Id = id; Name = name; Target = target; IsUtility = isUtility; IsCustom = isCustom;
        NameLower = name.ToLowerInvariant();
        var sb = new System.Text.StringBuilder();
        bool boundary = true;
        foreach (char c in NameLower)
        {
            if (char.IsLetterOrDigit(c)) { if (boundary) sb.Append(c); boundary = false; }
            else boundary = true;
        }
        Initials = sb.ToString();
    }

    public string Id { get; }
    public string Name { get; }

    /// <summary>Shell parsing name inside <c>shell:AppsFolder</c>, or a direct path for a user-added item.</summary>
    public string Target { get; }

    /// <summary>Installed but not something people open as an app (admin consoles, docs links, updaters...). Kept out of the grid, still searchable.</summary>
    public bool IsUtility { get; }

    /// <summary>A file, shortcut, or folder explicitly added by the user.</summary>
    public bool IsCustom { get; }

    public ImageSource? Icon { get; set; }

    internal string NameLower { get; }
    internal string Initials { get; }
}
