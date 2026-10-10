using Launchpad.Core.Apps;

namespace Launchpad.App.Apps;

/// <summary>Forgiving name search for the (few hundred) installed apps: prefix, word start, initials, then loose subsequence.</summary>
public static class AppSearch
{
    public static List<AppItem> Search(IReadOnlyList<AppItem> apps, string query, Func<AppItem, int>? usage = null, int limit = 60)
    {
        string q = query.Trim().ToLowerInvariant();
        if (q.Length == 0) return new List<AppItem>();
        string[] words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var scored = new List<(AppItem app, int score)>();
        foreach (var app in apps)
        {
            int s = Score(app, q, words);
            if (s <= 0) continue;
            if (usage != null) s += Math.Min(usage(app), 30) * 4;
            if (app.IsUtility) s = Math.Max(1, s - 150);   // a real app with the same match quality should come first
            scored.Add((app, s));
        }
        return scored.OrderByDescending(x => x.score)
                     .ThenBy(x => x.app.Name, StringComparer.CurrentCultureIgnoreCase)
                     .Take(limit).Select(x => x.app).ToList();
    }

    private static int Score(AppItem app, string q, string[] words) =>
        AppNameMatcher.Score(app.NameLower, app.Initials, q, words);
}
