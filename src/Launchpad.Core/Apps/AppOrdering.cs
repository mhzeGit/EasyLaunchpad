namespace Launchpad.Core.Apps;

public enum AppArrangement { Name, MostUsed, Custom }

/// <summary>How the app grid is ordered: alphabetical, most used first, or the user's own drag-and-drop order.</summary>
public static class AppOrdering
{
    public static AppArrangement Parse(string? mode) => mode?.ToLowerInvariant() switch
    {
        "mostused" or "usage" or "used" => AppArrangement.MostUsed,
        "custom" => AppArrangement.Custom,
        _ => AppArrangement.Name,
    };

    public static string ToSetting(AppArrangement a) => a switch
    {
        AppArrangement.MostUsed => "mostused",
        AppArrangement.Custom => "custom",
        _ => "name",
    };

    /// <summary>
    /// Orders <paramref name="apps"/>. In custom mode, apps missing from <paramref name="customOrder"/> (newly installed ones)
    /// go to the end, alphabetically, so nothing ever disappears.
    /// </summary>
    public static List<T> Apply<T>(IEnumerable<T> apps, AppArrangement mode, IReadOnlyList<string> customOrder,
        Func<T, string> id, Func<T, string> name, Func<T, int> launches)
    {
        var list = apps.ToList();
        var byName = StringComparer.CurrentCultureIgnoreCase;
        switch (mode)
        {
            case AppArrangement.MostUsed:
                return list.OrderByDescending(launches).ThenBy(name, byName).ToList();

            case AppArrangement.Custom:
                var rank = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < customOrder.Count; i++) rank.TryAdd(customOrder[i], i);
                return list.OrderBy(a => rank.TryGetValue(id(a), out int r) ? r : int.MaxValue).ThenBy(name, byName).ToList();

            default:
                return list.OrderBy(name, byName).ToList();
        }
    }

    /// <summary>
    /// The full order to store after the user rearranged the visible grid: the visible apps in their new order, followed by
    /// every other known app (hidden ones, utilities) in the order they had, so toggling those back on keeps sensible places.
    /// </summary>
    public static List<string> Commit(IEnumerable<string> visibleInNewOrder, IEnumerable<string> previousFullOrder)
    {
        var result = new List<string>(visibleInNewOrder);
        var seen = new HashSet<string>(result, StringComparer.Ordinal);
        foreach (string id in previousFullOrder)
            if (seen.Add(id)) result.Add(id);
        return result;
    }

    /// <summary>Moves one element to a new index (what dropping a dragged icon does to the list).</summary>
    public static void Move<T>(List<T> list, int from, int to)
    {
        if (from == to || from < 0 || from >= list.Count) return;
        to = Math.Clamp(to, 0, list.Count - 1);
        var item = list[from];
        list.RemoveAt(from);
        list.Insert(to, item);
    }
}
