namespace Launchpad.Core.Apps;

/// <summary>
/// Scores how closely an app name matches what was typed. The earlier in the name the match sits, the better:
/// whole name, then the start of the name, then the start of the 2nd, 3rd... word, then initials, then anywhere inside a word.
/// Within a tier a shorter name scores higher (the query covers more of it), so "ch" lists ChatGPT before Google Chrome.
/// </summary>
public static class AppNameMatcher
{
    private const int Exact = 1000;
    private const int Prefix = 900;          // the name (or its first word) starts with the query
    private const int FirstWordStart = 700;  // minus 50 for every word the match is pushed back by
    private const int MaxWordsCounted = 6;
    private const int Initials = 330;        // "vsc" -> Visual Studio Code
    private const int InsideWord = 250;
    private const int Loose = 120;           // letters in order, first letter starting a word

    /// <summary>First letter of every word of an already lower-cased name ("visual studio code" -> "vsc").</summary>
    public static string InitialsOf(string nameLower)
    {
        var sb = new System.Text.StringBuilder();
        bool boundary = true;
        foreach (char c in nameLower)
        {
            if (char.IsLetterOrDigit(c)) { if (boundary) sb.Append(c); boundary = false; }
            else boundary = true;
        }
        return sb.ToString();
    }

    /// <summary>
    /// 0 when <paramref name="nameLower"/> doesn't match, otherwise higher is closer. <paramref name="query"/> is the whole
    /// lower-cased query and <paramref name="words"/> its space-separated words; every word has to be found in the name.
    /// </summary>
    public static int Score(string nameLower, string initials, string query, string[] words)
    {
        if (query.Length == 0 || words.Length == 0) return 0;
        int lengthPenalty = Math.Min(nameLower.Length, 49);

        if (nameLower == query) return Exact;
        if (nameLower.StartsWith(query, StringComparison.Ordinal)) return Prefix - lengthPenalty;

        int total = 0;
        foreach (string w in words)
        {
            int s = WordScore(nameLower, w);
            if (s == 0) { total = 0; break; }
            total += s;
        }
        if (total > 0) return total / words.Length - lengthPenalty;

        if (words.Length == 1)
        {
            if (initials.StartsWith(query, StringComparison.Ordinal)) return Initials - lengthPenalty;
            if (query.Length >= 3 && StartsAWord(nameLower, query[0]) && IsSubsequence(query, nameLower))
                return Loose - lengthPenalty;
        }
        return 0;
    }

    /// <summary>Best place <paramref name="w"/> occurs in the name: at the start, at a later word's start, or inside a word.</summary>
    private static int WordScore(string name, string w)
    {
        int best = 0;
        int from = 0;
        while (true)
        {
            int idx = name.IndexOf(w, from, StringComparison.Ordinal);
            if (idx < 0) break;
            int s;
            if (idx == 0) s = Prefix;
            else if (!char.IsLetterOrDigit(name[idx - 1]))
                s = FirstWordStart - 50 * Math.Min(WordsBefore(name, idx), MaxWordsCounted);
            else s = InsideWord - Math.Min(idx, 19);
            if (s > best) best = s;
            from = idx + 1;
        }
        return best;
    }

    private static int WordsBefore(string name, int idx)
    {
        int words = 0;
        bool inWord = false;
        for (int i = 0; i < idx; i++)
        {
            bool letter = char.IsLetterOrDigit(name[i]);
            if (letter && !inWord) words++;
            inWord = letter;
        }
        return words;
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
