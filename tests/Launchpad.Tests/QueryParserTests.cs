using Launchpad.Core.Search;

namespace Launchpad.Tests;

public class QueryParserTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 15, 30, 0, DateTimeKind.Local);

    [Fact]
    public void PlainWordsBecomeAndedSubstringTerms()
    {
        var q = QueryParser.Parse("annual  report");
        Assert.Equal(new[] { "annual", "report" }, q.Terms.Select(t => t.Text));
        Assert.All(q.Terms, t => Assert.Equal(TermMode.Substring, t.Mode));
    }

    [Fact]
    public void QuotesKeepPhrasesTogether_AndDashNegates()
    {
        var q = QueryParser.Parse("\"annual report\" -draft");
        Assert.Equal("annual report", q.Terms[0].Text);
        Assert.True(q.Terms[1].Negate);
        Assert.Equal("draft", q.Terms[1].Text);
    }

    [Fact]
    public void ExtAndTypeFilters()
    {
        var q = QueryParser.Parse("ext:.PDF,docx type:image -ext:png");
        Assert.Contains("pdf", q.Extensions);
        Assert.Contains("docx", q.Extensions);
        Assert.Contains(FileCategory.Image, q.Categories);
        Assert.Contains("png", q.ExcludedExtensions);
        Assert.Empty(q.Terms);
    }

    [Fact]
    public void FolderAndFileKinds()
    {
        Assert.True(QueryParser.Parse("type:folder src").FoldersOnly);
        Assert.True(QueryParser.Parse("type:file").FilesOnly);
    }

    [Theory]
    [InlineData(">10mb", 10L * 1024 * 1024 + 1, null)]
    [InlineData("<=1kb", null, 1024L)]
    [InlineData("1mb..5mb", 1L << 20, 5L << 20)]
    [InlineData("empty", null, 0L)]
    [InlineData("1.5gb", (long)(1.5 * (1L << 30)), (long)(1.5 * (1L << 30)))]
    public void SizeFilters(string value, long? min, long? max)
    {
        var q = QueryParser.Parse("size:" + value);
        Assert.Null(q.Error);
        Assert.Equal(min, q.MinSize);
        Assert.Equal(max, q.MaxSize);
    }

    [Fact]
    public void DateFilters()
    {
        var today = QueryParser.Parse("date:today", Now);
        Assert.Equal(Now.Date.ToFileTimeUtc(), today.MinTime);
        Assert.Equal(Now.Date.AddDays(1).ToFileTimeUtc() - 1, today.MaxTime);

        var rel = QueryParser.Parse("modified:7d", Now);
        Assert.Equal(Now.AddDays(-7).ToFileTimeUtc(), rel.MinTime);
        Assert.Null(rel.MaxTime);

        var after = QueryParser.Parse("date:>2024-01-01", Now);
        Assert.Equal(new DateTime(2024, 1, 2).ToFileTimeUtc(), after.MinTime);

        var range = QueryParser.Parse("date:2024-01-01..2024-01-31", Now);
        Assert.Equal(new DateTime(2024, 1, 1).ToFileTimeUtc(), range.MinTime);
        Assert.Equal(new DateTime(2024, 2, 1).ToFileTimeUtc() - 1, range.MaxTime);
    }

    [Fact]
    public void WildcardsPathsAndDrivePrefixes()
    {
        var q = QueryParser.Parse("inv*ce users\\docs c:\\temp");
        Assert.Equal(TermMode.Wildcard, q.Terms[0].Mode);
        Assert.Equal(TermMode.Path, q.Terms[1].Mode);
        Assert.Equal(TermMode.Path, q.Terms[2].Mode);   // "c:" must not be mistaken for a key
    }

    [Fact]
    public void CommonWildcardShapesBecomeCheapFilters()
    {
        var q = QueryParser.Parse("*.PDF -*.tmp *draft* rep*ort a?c.*");
        Assert.Contains("pdf", q.Extensions);
        Assert.Contains("tmp", q.ExcludedExtensions);
        Assert.Equal(new[] { TermMode.Substring, TermMode.Wildcard, TermMode.Wildcard }, q.Terms.Select(t => t.Mode));
        Assert.Equal("draft", q.Terms[0].Text);
    }

    [Fact]
    public void BadInputReportsErrorsInsteadOfThrowing()
    {
        Assert.NotNull(QueryParser.Parse("size:lots").Error);
        Assert.NotNull(QueryParser.Parse("regex:(").Error);
        Assert.NotNull(QueryParser.Parse("type:bogus").Error);
        Assert.NotNull(QueryParser.Parse("date:someday").Error);
    }

    [Fact]
    public void InFilter()
    {
        Assert.Equal(@"C:\Projects", QueryParser.Parse(@"in:C:\Projects\ foo").InPath);
    }
}
