namespace Launchpad.Core.Apps;

/// <summary>A folder of apps in the launcher grid (created by dropping one icon onto another).</summary>
public sealed class AppGroup
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = AppGroups.DefaultName;

    /// <summary>Member app ids, in the order shown inside the group.</summary>
    public List<string> AppIds { get; set; } = new();
}

/// <summary>Pure operations on the user's groups, so the rules are testable without any UI.</summary>
public static class AppGroups
{
    public const string DefaultName = "Group";
    private const string IdPrefix = "g";

    public static bool IsGroupId(string id) => id.Length > 1 && id[0] == 'g' && id.Length < 12 && !id.Contains(' ') && id.Skip(1).All(Uri.IsHexDigit) && id.Length == 9;

    /// <summary>Makes a new group of two apps and adds it to <paramref name="groups"/>. Both apps leave any group they were in.</summary>
    public static AppGroup Create(List<AppGroup> groups, string firstAppId, string secondAppId, string? name = null)
    {
        RemoveApp(groups, firstAppId);
        RemoveApp(groups, secondAppId);
        var g = new AppGroup
        {
            Id = IdPrefix + Guid.NewGuid().ToString("N")[..8],
            Name = string.IsNullOrWhiteSpace(name) ? DefaultName : name.Trim(),
            AppIds = { firstAppId, secondAppId },
        };
        groups.Add(g);
        return g;
    }

    /// <summary>Adds an app to a group (moving it out of any other group). Returns false if it was already a member.</summary>
    public static bool AddTo(List<AppGroup> groups, AppGroup target, string appId)
    {
        if (target.AppIds.Contains(appId)) return false;
        RemoveApp(groups, appId);
        target.AppIds.Add(appId);
        return true;
    }

    /// <summary>
    /// Takes an app out of whichever group holds it. A group left with fewer than two apps is dissolved.
    /// Returns the ids that are free-standing again as a result (the app itself, plus a dissolved group's last member).
    /// </summary>
    public static List<string> RemoveApp(List<AppGroup> groups, string appId)
    {
        var freed = new List<string>();
        foreach (var g in groups.ToList())
        {
            if (!g.AppIds.Remove(appId)) continue;
            freed.Add(appId);
            if (g.AppIds.Count < 2)
            {
                freed.AddRange(g.AppIds);
                groups.Remove(g);
            }
        }
        return freed;
    }

    public static void Dissolve(List<AppGroup> groups, string groupId) => groups.RemoveAll(g => g.Id == groupId);

    public static HashSet<string> GroupedAppIds(IEnumerable<AppGroup> groups)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in groups) foreach (var id in g.AppIds) set.Add(id);
        return set;
    }

    /// <summary>
    /// The grid order after dropping <paramref name="draggedId"/> onto <paramref name="targetId"/>: the dragged item disappears
    /// (it's inside the group now) and the target's slot is taken by <paramref name="resultingGroupId"/>.
    /// </summary>
    public static List<string> OrderAfterGrouping(IEnumerable<string> order, string draggedId, string targetId, string resultingGroupId)
    {
        var result = new List<string>();
        foreach (string id in order)
        {
            if (id == draggedId) continue;
            result.Add(id == targetId ? resultingGroupId : id);
        }
        return result;
    }
}
