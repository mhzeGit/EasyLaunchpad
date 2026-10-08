using Launchpad.Core.Apps;

namespace Launchpad.Tests;

public class AppOrderingTests
{
    private record App(string Id, string Name, int Launches = 0);

    private static readonly App[] Apps =
    {
        new("a", "Zed"), new("b", "alpha", 5), new("c", "Mango", 9), new("d", "Beta", 5),
    };

    private static List<string> Names(AppArrangement mode, params string[] custom) =>
        AppOrdering.Apply(Apps, mode, custom, a => a.Id, a => a.Name, a => a.Launches).Select(a => a.Name).ToList();

    [Fact] public void NameIsCaseInsensitiveAlphabetical() =>
        Assert.Equal(new[] { "alpha", "Beta", "Mango", "Zed" }, Names(AppArrangement.Name));

    [Fact] public void MostUsedFirst_TiesByName() =>
        Assert.Equal(new[] { "Mango", "alpha", "Beta", "Zed" }, Names(AppArrangement.MostUsed));

    [Fact] public void CustomFollowsTheStoredOrder() =>
        Assert.Equal(new[] { "Zed", "Mango", "alpha", "Beta" }, Names(AppArrangement.Custom, "a", "c", "b", "d"));

    [Fact] public void CustomPutsNewAndUnknownAppsLastAlphabetically() =>
        Assert.Equal(new[] { "Mango", "alpha", "Beta", "Zed" }, Names(AppArrangement.Custom, "c"));

    [Fact] public void CustomIgnoresStoredIdsThatNoLongerExist() =>
        Assert.Equal(new[] { "Beta", "alpha", "Mango", "Zed" }, Names(AppArrangement.Custom, "gone", "d", "b"));

    [Fact] public void CommitKeepsVisibleOrderThenTheRestWithoutDuplicates()
    {
        var full = AppOrdering.Commit(new[] { "c", "a" }, new[] { "a", "x", "b", "c", "y" });
        Assert.Equal(new[] { "c", "a", "x", "b", "y" }, full);
    }

    [Theory]
    [InlineData(0, 3, "b c d a")]
    [InlineData(3, 0, "d a b c")]
    [InlineData(1, 2, "a c b d")]
    [InlineData(2, 2, "a b c d")]
    [InlineData(1, 99, "a c d b")]    // dropping past the end clamps
    public void MoveBehavesLikeDroppingAnIcon(int from, int to, string expected)
    {
        var list = new List<string> { "a", "b", "c", "d" };
        AppOrdering.Move(list, from, to);
        Assert.Equal(expected, string.Join(' ', list));
    }

    [Fact] public void ModeSettingRoundTrips()
    {
        foreach (var m in Enum.GetValues<AppArrangement>())
            Assert.Equal(m, AppOrdering.Parse(AppOrdering.ToSetting(m)));
        Assert.Equal(AppArrangement.Name, AppOrdering.Parse(null));
        Assert.Equal(AppArrangement.Name, AppOrdering.Parse("whatever"));
    }
}
