using Launchpad.Core.Index;
using Launchpad.Core.Search;

namespace Launchpad.Tests;

public class SearchEngineTests
{
    private static long Ft(int y, int m, int d) => new DateTime(y, m, d, 12, 0, 0, DateTimeKind.Local).ToFileTimeUtc();

    private static FileIndex Build()
    {
        var ix = new FileIndex(16);
        int c = ix.Add(-1, "C:", EntryFlags.Directory, 0, 0);
        int users = ix.Add(c, "Users", EntryFlags.Directory, 0, 0);
        int me = ix.Add(users, "me", EntryFlags.Directory, 0, 0);
        int docs = ix.Add(me, "Documents", EntryFlags.Directory, 0, 0);
        int proj = ix.Add(me, "Projects", EntryFlags.Directory, 0, 0);
        ix.Add(docs, "Annual Report 2024.pdf", EntryFlags.None, 2_000_000, Ft(2024, 3, 1));
        ix.Add(docs, "report.docx", EntryFlags.None, 40_000, Ft(2025, 6, 1));
        ix.Add(docs, "Report Draft.docx", EntryFlags.None, 41_000, Ft(2025, 6, 2));
        ix.Add(docs, "holiday.jpg", EntryFlags.None, 3_500_000, Ft(2023, 8, 8));
        ix.Add(docs, "holiday.png", EntryFlags.None, 5_500_000, Ft(2023, 8, 9));
        ix.Add(proj, "reporter.cs", EntryFlags.None, 900, Ft(2026, 1, 1));
        ix.Add(proj, "ReportService.cs", EntryFlags.None, 1900, Ft(2026, 2, 1));
        ix.Add(proj, "notes.txt", EntryFlags.None, 0, Ft(2026, 9, 30));
        int nm = ix.Add(proj, "node_modules", EntryFlags.Directory, 0, 0);
        ix.Add(nm, "report.js", EntryFlags.None, 100, Ft(2026, 1, 1));
        int old = ix.Add(c, "Old", EntryFlags.Directory, 0, 0);
        ix.Add(old, "report.old", EntryFlags.None, 1, Ft(2010, 1, 1));
        return ix;
    }

    private static List<string> Names(FileIndex ix, string query, SortKey sort = SortKey.Name, bool desc = false, int limit = 100)
    {
        var res = SearchEngine.Run(ix, QueryParser.Parse(query), new SearchOptions { Sort = sort, Descending = desc, Limit = limit, UserProfile = @"C:\Users\me" });
        Assert.Null(res.Error);
        return res.Hits.Select(h => h.Name).ToList();
    }

    [Fact]
    public void SubstringMatchIsCaseInsensitiveAndAnded()
    {
        var ix = Build();
        Assert.Equal(new[] { "Annual Report 2024.pdf" }, Names(ix, "annual REPORT"));
        Assert.Equal(7, Names(ix, "report").Count);
    }

    [Fact]
    public void NegationAndWildcard()
    {
        var ix = Build();
        Assert.DoesNotContain("Report Draft.docx", Names(ix, "report -draft"));
        Assert.Equal(new[] { "report.docx", "Report Draft.docx" }.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), Names(ix, "report*.docx"));
        Assert.Equal(new[] { "holiday.jpg" }, Names(ix, "holi?ay.j*"));
    }

    [Fact]
    public void ExtensionTypeAndKindFilters()
    {
        var ix = Build();
        Assert.Equal(new[] { "holiday.jpg", "holiday.png" }, Names(ix, "type:image"));
        Assert.Equal(new[] { "holiday.jpg" }, Names(ix, "type:image -ext:png"));
        Assert.Equal(new[] { "report.docx", "Report Draft.docx" }.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), Names(ix, "ext:docx"));
        Assert.Equal(new[] { "Documents", "node_modules", "Old", "Projects" }, Names(ix, "type:folder o"));
        Assert.Equal(new[] { "Documents" }, Names(ix, "type:folder docu"));
        Assert.DoesNotContain("Documents", Names(ix, "type:file o"));
    }

    [Fact]
    public void SizeAndDateFilters()
    {
        var ix = Build();
        Assert.Equal(new[] { "holiday.jpg", "holiday.png" }, Names(ix, "size:>3mb"));
        Assert.Equal(new[] { "notes.txt" }, Names(ix, "size:empty type:file"));
        Assert.Equal(new[] { "report.old" }, Names(ix, "date:<2020-01-01 type:file"));
        Assert.Equal(new[] { "holiday.jpg", "holiday.png" }, Names(ix, "date:2023-08-01..2023-08-31"));
    }

    [Fact]
    public void PathAndInFilters()
    {
        var ix = Build();
        Assert.Equal(new[] { "reporter.cs", "ReportService.cs", "report.js" }.OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
            Names(ix, @"report in:C:\Users\me\Projects"));
        Assert.Equal(new[] { "report.js" }, Names(ix, @"report node_modules\"));
        Assert.Empty(Names(ix, @"report in:C:\DoesNotExist"));
    }

    [Fact]
    public void RegexMatchesNames()
    {
        var ix = Build();
        Assert.Equal(new[] { "reporter.cs", "ReportService.cs" }.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), Names(ix, @"regex:^report(er|service)\.cs$"));
    }

    [Fact]
    public void RelevancePrefersExactThenPrefixThenBoundaryAndUserFolders()
    {
        var ix = Build();
        var names = Names(ix, "report", SortKey.Relevance);
        Assert.Equal("report.docx", names[0]);              // exact stem match, user folder
        Assert.True(names.IndexOf("report.js") > names.IndexOf("Report Draft.docx"));   // node_modules is pushed down
        Assert.True(names.IndexOf("report.old") < names.IndexOf("ReportService.cs"));   // an exact stem match still beats a prefix match
        Assert.True(names.IndexOf("reporter.cs") < names.IndexOf("ReportService.cs"));   // same match quality: the shorter name wins
        Assert.True(names.IndexOf("Annual Report 2024.pdf") > names.IndexOf("Report Draft.docx"));   // word-boundary < prefix
    }

    [Fact]
    public void MachineGeneratedFoldersSinkBelowRealMatches()
    {
        var ix = new FileIndex(16);
        int c = ix.Add(-1, "C:", EntryFlags.Directory, 0, 0);
        int me = ix.Add(ix.Add(c, "Users", EntryFlags.Directory, 0, 0), "me", EntryFlags.Directory, 0, 0);
        int docs = ix.Add(me, "Documents", EntryFlags.Directory, 0, 0);
        ix.Add(docs, "Quarterly report final.docx", EntryFlags.None, 10, 1);            // boundary match, but real
        int venv = ix.Add(ix.Add(ix.Add(ix.Add(me, "proj", EntryFlags.Directory, 0, 0), ".venv", EntryFlags.Directory, 0, 0), "Lib", EntryFlags.Directory, 0, 0), "site-packages", EntryFlags.Directory, 0, 0);
        ix.Add(venv, "report.py", EntryFlags.None, 10, 1);                              // exact stem match in noise
        var names = Names(ix, "report", SortKey.Relevance);
        Assert.Equal("Quarterly report final.docx", names[0]);
    }

    [Fact]
    public void SortingBySizeModifiedAndDirection()
    {
        var ix = Build();
        Assert.Equal(new[] { "holiday.png", "holiday.jpg" }, Names(ix, "holiday", SortKey.Size, desc: true));
        Assert.Equal("Annual Report 2024.pdf", Names(ix, "report type:file ext:pdf,docx", SortKey.Modified)[0]);
        Assert.Equal("Report Draft.docx", Names(ix, "report type:file ext:pdf,docx", SortKey.Modified, desc: true)[0]);
        Assert.Equal(new[] { "reporter.cs", "ReportService.cs" }.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), Names(ix, "report ext:cs", SortKey.Type));
    }

    [Fact]
    public void LimitKeepsTotalAndDeletedEntriesAreIgnored()
    {
        var ix = Build();
        var res = SearchEngine.Run(ix, QueryParser.Parse("report"), new SearchOptions { Limit = 2, Sort = SortKey.Name });
        Assert.Equal(2, res.Hits.Count);
        Assert.Equal(7, res.Total);

        using (ix.WriteLock()) ix.Remove(ix.Resolve(@"C:\Users\me\Documents"));
        Assert.Equal(4, SearchEngine.Run(ix, QueryParser.Parse("report"), new SearchOptions()).Total);   // documents' children are orphaned
    }

    [Fact]
    public void TopKHeapPathAgreesWithFullSort()
    {
        var ix = new FileIndex(16);
        int root = ix.Add(-1, "D:", EntryFlags.Directory, 0, 0);
        var rnd = new Random(7);
        for (int i = 0; i < 150_000; i++) ix.Add(root, $"item{rnd.Next(1_000_000):D7}.dat", EntryFlags.None, rnd.Next(1_000_000), i);

        var q = QueryParser.Parse("item");
        var top = SearchEngine.Run(ix, q, new SearchOptions { Limit = 50, Sort = SortKey.Size, Descending = true });
        var expected = Enumerable.Range(0, ix.Count).OrderByDescending(i => ix.Size[i]).ThenBy(i => ix.GetName(i), StringComparer.OrdinalIgnoreCase)
            .Take(50).Select(i => ix.Size[i]).ToList();
        Assert.Equal(150_000, top.Total);
        Assert.Equal(expected.Take(10), top.Hits.Select(h => h.Size).Take(10));
        Assert.True(top.Hits.Zip(top.Hits.Skip(1)).All(p => p.First.Size >= p.Second.Size));
    }

    [Theory]
    [InlineData("*.pdf", "a.PDF", true)]
    [InlineData("*.pdf", "a.pdfx", false)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("a*b*c", "aXXbYY", false)]
    [InlineData("??.txt", "ab.txt", true)]
    [InlineData("??.txt", "abc.txt", false)]
    [InlineData("*", "", true)]
    public void Glob(string pattern, string text, bool expected) =>
        Assert.Equal(expected, SearchEngine.Glob(pattern, text));
}
