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

    private static int Score(AppItem app, string q, string[] words)
    {
        string n = app.NameLower;
        if (n == q) return 1000;
        if (n.StartsWith(q, StringComparison.Ordinal)) return 800 - Math.Min(n.Length, 100);

        // every typed word must be found; reward word starts
        int total = 0;
        foreach (string w in words)
        {
            int idx = n.IndexOf(w, StringComparison.Ordinal);
            if (idx < 0) { total = -1; break; }
            total += idx == 0 || !char.IsLetterOrDigit(n[idx - 1]) ? 400 : 200;
        }
        if (total > 0) return total - Math.Min(n.Length, 100);

        if (words.Length == 1)
        {
            if (app.Initials.StartsWith(q, StringComparison.Ordinal)) return 350;   // "vsc" -> Visual Studio Code
            // loose "letters in order" match, but only when the first letter starts a word, so "event" doesn't find "Developer Command Prompt"
            if (q.Length >= 3 && StartsAWord(n, q[0]) && IsSubsequence(q, n)) return 120;
        }
        return 0;
    }

    private static bool StartsAWord(string name, char c)
    {
        for (int i = 0; i < name.Length; i++)
            if (name[i] == c && (i == 0 || !char.IsLetterOrDigit(name[i - 1]))) return true;
        return false;
    }

    private static bool IsSubsequence(string needle, string hay)
    {
        int i = 0;
        foreach (char c in hay)
            if (i < needle.Length && c == needle[i]) i++;
        return i == needle.Length;
    }
}
