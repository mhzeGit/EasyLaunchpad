using System.Text.RegularExpressions;

namespace Launchpad.Core.Search;

public enum TermMode { Substring, Wildcard, Regex, Path }

public sealed class Term
{
    public string Text { get; init; } = "";
    public TermMode Mode { get; init; }
    public bool Negate { get; init; }
    public Regex? Regex { get; init; }
}

/// <summary>A parsed search. Free text terms are ANDed; every filter narrows further.</summary>
public sealed class Query
{
    public List<Term> Terms { get; } = new();
    public bool FoldersOnly { get; set; }
    public bool FilesOnly { get; set; }
    public HashSet<string> Extensions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ExcludedExtensions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<FileCategory> Categories { get; } = new();
    public long? MinSize { get; set; }
    public long? MaxSize { get; set; }
    public long? MinTime { get; set; }   // FILETIME ticks (UTC)
    public long? MaxTime { get; set; }
    public string? InPath { get; set; }
    public bool IncludeHidden { get; set; } = true;
    public string? Error { get; set; }

    public bool HasPositiveTerms => Terms.Any(t => !t.Negate);

    public bool IsEmpty =>
        Terms.Count == 0 && !FoldersOnly && !FilesOnly && Extensions.Count == 0 && ExcludedExtensions.Count == 0 &&
        Categories.Count == 0 && MinSize == null && MaxSize == null && MinTime == null && MaxTime == null && InPath == null;
}

/// <summary>
/// Query language:
///   report                 substring match on the name (all words must match)
///   "annual report"        exact phrase
///   -draft                 exclude
///   *.pdf  inv?ice*        wildcards (match the whole name)
///   users\docs             contains a path separator: match against the full path
///   ext:pdf,docx           extensions            type:image|video|audio|doc|code|archive|app|folder|file
///   size:&gt;10mb  size:1mb..5mb  size:empty|tiny|small|medium|large|huge
///   date:today|yesterday|week|month|year|7d|2w|&gt;2024-01-01|2024-01-01..2024-03-01
///   in:C:\Projects         only below a folder    path:foo   match anywhere in the full path
///   regex:^img_\d+         regular expression on the name
/// </summary>
public static class QueryParser
{
    public static Query Parse(string? text, DateTime? nowLocal = null)
    {
        var q = new Query();
        if (string.IsNullOrWhiteSpace(text)) return q;
        DateTime now = nowLocal ?? DateTime.Now;

        foreach (string raw in Tokenize(text)) ParseAtom(q, raw, now);
        return q;
    }

    private static void ParseAtom(Query q, string tok, DateTime now)
    {
        bool negate = false;
        if (tok.Length > 1 && (tok[0] == '-' || tok[0] == '!')) { negate = true; tok = tok[1..]; }
        if (tok.Length == 0) return;
        int colon = tok.IndexOf(':');
        if (colon > 0 && colon < tok.Length - 1 && TryKeyed(q, tok[..colon].ToLowerInvariant(), Unquote(tok[(colon + 1)..]), negate, now)) return;
        AddTextTerm(q, Unquote(tok), negate);
    }

    private static bool TryKeyed(Query q, string key, string value, bool negate, DateTime now)
    {
        switch (key)
        {
            case "ext":
            case "extension":
                foreach (string e in value.Split(new[] { ',', '|', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    (negate ? q.ExcludedExtensions : q.Extensions).Add(e.TrimStart('.').ToLowerInvariant());
                return true;

            case "type":
            case "kind":
            case "is":
                foreach (string w in value.Split(new[] { ',', '|', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string lw = w.ToLowerInvariant();
                    if (lw is "folder" or "folders" or "dir" or "directory" or "directories") { if (negate) q.FilesOnly = true; else q.FoldersOnly = true; }
                    else if (lw is "file" or "files") { if (negate) q.FoldersOnly = true; else q.FilesOnly = true; }
                    else if (FileTypes.TryParseCategory(lw, out var cat))
                    {
                        if (negate) foreach (string e in FileTypes.Extensions(cat)) q.ExcludedExtensions.Add(e);
                        else q.Categories.Add(cat);
                    }
                    else { q.Error = $"Unknown type \"{w}\""; }
                }
                return true;

            case "size":
            case "s":
                if (!TryParseSize(value, out long? min, out long? max)) { q.Error = $"Can't read size \"{value}\""; return true; }
                if (min != null) q.MinSize = Math.Max(q.MinSize ?? 0, min.Value);
                if (max != null) q.MaxSize = Math.Min(q.MaxSize ?? long.MaxValue, max.Value);
                return true;

            case "date":
            case "modified":
            case "mod":
            case "dm":
            case "changed":
                if (!TryParseDate(value, now, out long? from, out long? to)) { q.Error = $"Can't read date \"{value}\""; return true; }
                if (from != null) q.MinTime = Math.Max(q.MinTime ?? 0, from.Value);
                if (to != null) q.MaxTime = Math.Min(q.MaxTime ?? long.MaxValue, to.Value);
                return true;

            case "in":
            case "under":
            case "folder":
                q.InPath = value.Replace('/', '\\').TrimEnd('\\');
                return true;

            case "path":
                q.Terms.Add(new Term { Text = value.Replace('/', '\\'), Mode = TermMode.Path, Negate = negate });
                return true;

            case "name":
                AddTextTerm(q, value, negate);
                return true;

            case "regex":
            case "re":
                try
                {
                    var rx = new Regex(value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                    q.Terms.Add(new Term { Text = value, Mode = TermMode.Regex, Negate = negate, Regex = rx });
                }
                catch (ArgumentException ex) { q.Error = "Invalid regex: " + ex.Message; }
                return true;

            case "hidden":
                q.IncludeHidden = !value.Equals("no", StringComparison.OrdinalIgnoreCase) && !value.Equals("false", StringComparison.OrdinalIgnoreCase);
                return true;
        }
        return false;
    }

    private static void AddTextTerm(Query q, string text, bool negate)
    {
        if (text.Length == 0) return;
        if (text.Contains('\\') || text.Contains('/'))
            q.Terms.Add(new Term { Text = text.Replace('/', '\\'), Mode = TermMode.Path, Negate = negate });
        else if (text.Contains('*') || text.Contains('?'))
        {
            // Common shapes get rewritten to the cheap filters: "*.pdf" is an extension test, "*foo*" a substring test.
            if (text.Length > 2 && text[0] == '*' && text[1] == '.' && text.AsSpan(2).IndexOfAny("*?.") < 0)
                (negate ? q.ExcludedExtensions : q.Extensions).Add(text[2..].ToLowerInvariant());
            else if (text.Length > 2 && text[0] == '*' && text[^1] == '*' && text.AsSpan(1, text.Length - 2).IndexOfAny("*?") < 0)
                q.Terms.Add(new Term { Text = text[1..^1], Mode = TermMode.Substring, Negate = negate });
            else
                q.Terms.Add(new Term { Text = text, Mode = TermMode.Wildcard, Negate = negate });
        }
        else
            q.Terms.Add(new Term { Text = text, Mode = TermMode.Substring, Negate = negate });
    }

    // ------------------------------------------------------------------ tokenizing

    private static IEnumerable<string> Tokenize(string s)
    {
        var sb = new System.Text.StringBuilder();
        bool inQuote = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"') { inQuote = !inQuote; sb.Append(c); continue; }
            if (char.IsWhiteSpace(c) && !inQuote)
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static string Unquote(string s) => s.Replace("\"", "");

    // ------------------------------------------------------------------ sizes

    private static readonly Dictionary<string, long> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        [""] = 1, ["b"] = 1,
        ["k"] = 1L << 10, ["kb"] = 1L << 10, ["kib"] = 1L << 10,
        ["m"] = 1L << 20, ["mb"] = 1L << 20, ["mib"] = 1L << 20,
        ["g"] = 1L << 30, ["gb"] = 1L << 30, ["gib"] = 1L << 30,
        ["t"] = 1L << 40, ["tb"] = 1L << 40, ["tib"] = 1L << 40,
    };

    internal static bool TryParseSize(string v, out long? min, out long? max)
    {
        min = max = null;
        v = v.Trim();
        switch (v.ToLowerInvariant())
        {
            case "empty": max = 0; return true;
            case "tiny": min = 1; max = 10 * 1024; return true;
            case "small": min = 10 * 1024 + 1; max = 1L << 20; return true;
            case "medium": min = (1L << 20) + 1; max = 100L << 20; return true;
            case "large": min = (100L << 20) + 1; max = 1L << 30; return true;
            case "huge": case "giant": min = (1L << 30) + 1; return true;
        }

        int dots = v.IndexOf("..", StringComparison.Ordinal);
        if (dots >= 0)
        {
            string a = v[..dots], b = v[(dots + 2)..];
            if (a.Length > 0) { if (!TryParseBytes(a, out long lo)) return false; min = lo; }
            if (b.Length > 0) { if (!TryParseBytes(b, out long hi)) return false; max = hi; }
            return min != null || max != null;
        }

        string op = "=";
        foreach (string candidate in new[] { ">=", "<=", ">", "<", "=" })
            if (v.StartsWith(candidate, StringComparison.Ordinal)) { op = candidate; v = v[candidate.Length..]; break; }

        if (!TryParseBytes(v, out long n)) return false;
        switch (op)
        {
            case ">": min = n + 1; break;
            case ">=": min = n; break;
            case "<": max = n - 1; break;
            case "<=": max = n; break;
            default: min = n; max = n; break;
        }
        return true;
    }

    private static bool TryParseBytes(string s, out long bytes)
    {
        bytes = 0;
        int i = 0;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
        if (i == 0) return false;
        if (!double.TryParse(s.AsSpan(0, i), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double n)) return false;
        if (!Units.TryGetValue(s[i..].Trim(), out long mult)) return false;
        bytes = (long)(n * mult);
        return true;
    }

    // ------------------------------------------------------------------ dates

    internal static bool TryParseDate(string v, DateTime now, out long? from, out long? to)
    {
        from = to = null;
        v = v.Trim().ToLowerInvariant();
        DateTime today = now.Date;

        switch (v)
        {
            case "today": return Range(today, today.AddDays(1), out from, out to);
            case "yesterday": return Range(today.AddDays(-1), today, out from, out to);
            case "week": case "thisweek": return Range(now.AddDays(-7), null, out from, out to);
            case "month": case "thismonth": return Range(now.AddMonths(-1), null, out from, out to);
            case "year": case "thisyear": return Range(now.AddYears(-1), null, out from, out to);
        }

        // relative: 90m (minutes) is ambiguous with months, so months use "mo".
        var rel = Regex.Match(v, @"^(\d+)(h|d|w|mo|y)$");
        if (rel.Success)
        {
            int n = int.Parse(rel.Groups[1].Value);
            DateTime start = rel.Groups[2].Value switch
            {
                "h" => now.AddHours(-n),
                "d" => now.AddDays(-n),
                "w" => now.AddDays(-7 * n),
                "mo" => now.AddMonths(-n),
                _ => now.AddYears(-n),
            };
            return Range(start, null, out from, out to);
        }

        int dots = v.IndexOf("..", StringComparison.Ordinal);
        if (dots >= 0)
        {
            string a = v[..dots], b = v[(dots + 2)..];
            DateTime? lo = null, hi = null;
            if (a.Length > 0) { if (!TryDay(a, out var d)) return false; lo = d; }
            if (b.Length > 0) { if (!TryDay(b, out var d)) return false; hi = d.AddDays(1); }
            return Range(lo, hi, out from, out to);
        }

        string op = "=";
        foreach (string c in new[] { ">=", "<=", ">", "<", "=" })
            if (v.StartsWith(c, StringComparison.Ordinal)) { op = c; v = v[c.Length..]; break; }

        if (!TryDay(v, out DateTime day)) return false;
        return op switch
        {
            ">" => Range(day.AddDays(1), null, out from, out to),
            ">=" => Range(day, null, out from, out to),
            "<" => Range(null, day, out from, out to),
            "<=" => Range(null, day.AddDays(1), out from, out to),
            _ => Range(day, day.AddDays(1), out from, out to),
        };
    }

    private static bool TryDay(string s, out DateTime day)
    {
        string[] formats = { "yyyy-MM-dd", "yyyy/MM/dd", "yyyyMMdd", "yyyy-MM", "yyyy" };
        if (DateTime.TryParseExact(s, formats, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal, out day)) { day = day.Date; return true; }
        return false;
    }

    private static bool Range(DateTime? from, DateTime? to, out long? f, out long? t)
    {
        f = from == null ? null : ToFileTime(from.Value);
        t = to == null ? null : ToFileTime(to.Value) - 1;   // exclusive end
        return true;
    }

    private static long ToFileTime(DateTime local) => local.ToFileTimeUtc();
}
