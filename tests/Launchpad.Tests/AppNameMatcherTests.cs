using Launchpad.Core.Apps;

namespace Launchpad.Tests;

public class AppNameMatcherTests
{
    private static int Score(string name, string query)
    {
        string n = name.ToLowerInvariant();
        string q = query.Trim().ToLowerInvariant();
        return AppNameMatcher.Score(n, AppNameMatcher.InitialsOf(n), q, q.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static List<string> Rank(string query, params string[] names) =>
        names.Select(n => (n, s: Score(n, query))).Where(x => x.s > 0)
             .OrderByDescending(x => x.s).Select(x => x.n).ToList();

    [Fact] public void NameStartingWithQueryBeatsLaterWordStart() =>
        Assert.Equal(new[] { "ChatGPT", "Google Chrome" }, Rank("ch", "Google Chrome", "ChatGPT"));

    [Fact] public void EarlierWordBeatsLaterWord() =>
        Assert.Equal(new[] { "Edge Chrome", "Visual Studio Chrome" }, Rank("ch", "Visual Studio Chrome", "Edge Chrome"));

    [Fact] public void WordStartBeatsMidWord() =>
        Assert.Equal(new[] { "Google Chrome", "Archive Tool" }, Rank("ch", "Archive Tool", "Google Chrome"));

    [Fact] public void ShorterNameWinsWithinTheSameTier() =>
        Assert.Equal(new[] { "Chrome", "ChatGPT", "Cherry Studio" }, Rank("ch", "Cherry Studio", "ChatGPT", "Chrome"));

    [Fact] public void ExactNameIsFirst() =>
        Assert.Equal(new[] { "Chrome", "Chrome Remote Desktop" }, Rank("chrome", "Chrome Remote Desktop", "Chrome"));

    [Fact] public void LaterOccurrenceAtAWordStartIsFound() =>
        Assert.True(Score("Archive Chrome", "ch") > Score("Archive Tool", "ch"));

    [Fact] public void InitialsMatchRanksBelowWordStartsAndAboveLooseLetters()
    {
        int initials = Score("Visual Studio Code", "vsc");
        Assert.True(initials > Score("Visual Studio Code", "vsd"));   // loose letters-in-order ("v..s..d") scores lower
        Assert.True(Score("Edge Chrome", "ch") > initials);
    }

    [Fact] public void EveryWordMustMatch() =>
        Assert.Equal(0, Score("Google Chrome", "chrome edge"));

    [Fact] public void MultiWordQueryPrefersNameStart() =>
        Assert.Equal(new[] { "Visual Studio Code", "Microsoft Visual Studio" }, Rank("visual studio", "Microsoft Visual Studio", "Visual Studio Code"));

    [Fact] public void NoMatchIsZero() => Assert.Equal(0, Score("Notepad", "zzz"));
}
