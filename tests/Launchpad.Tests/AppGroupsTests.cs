using Launchpad.Core.Apps;

namespace Launchpad.Tests;

public class AppGroupsTests
{
    [Fact]
    public void CreateMakesAGroupOfTwoWithADefaultName()
    {
        var groups = new List<AppGroup>();
        var g = AppGroups.Create(groups, "a", "b");
        Assert.Single(groups);
        Assert.Equal(new[] { "a", "b" }, g.AppIds);
        Assert.Equal(AppGroups.DefaultName, g.Name);
        Assert.True(AppGroups.IsGroupId(g.Id), g.Id);
    }

    [Fact]
    public void GroupIdsNeverCollideWithAppIds()
    {
        Assert.False(AppGroups.IsGroupId("67548e746da1a805"));   // 16 hex chars: an app id
        Assert.False(AppGroups.IsGroupId("gabc"));
        Assert.True(AppGroups.IsGroupId("g0123abcd"));
    }

    [Fact]
    public void AddToJoinsAnExistingGroupAndLeavesTheOldOne()
    {
        var groups = new List<AppGroup>();
        var g1 = AppGroups.Create(groups, "a", "b");
        var g2 = AppGroups.Create(groups, "c", "d");
        g2.AppIds.Add("e");

        Assert.True(AppGroups.AddTo(groups, g2, "a"));
        Assert.Equal(new[] { "c", "d", "e", "a" }, g2.AppIds);
        // g1 had only a and b: with a gone it dissolves, freeing b
        Assert.DoesNotContain(g1, groups);
        Assert.False(AppGroups.AddTo(groups, g2, "a"));   // already a member
    }

    [Fact]
    public void RemovingTheSecondToLastAppDissolvesTheGroup()
    {
        var groups = new List<AppGroup>();
        var g = AppGroups.Create(groups, "a", "b");
        var freed = AppGroups.RemoveApp(groups, "a");
        Assert.Empty(groups);
        Assert.Equal(new[] { "a", "b" }, freed.OrderBy(x => x));
        Assert.Equal(2, g.AppIds.Count + 1);   // (the object itself is untouched except for the removed id)
    }

    [Fact]
    public void RemovingFromABiggerGroupKeepsIt()
    {
        var groups = new List<AppGroup>();
        var g = AppGroups.Create(groups, "a", "b");
        g.AppIds.Add("c");
        var freed = AppGroups.RemoveApp(groups, "b");
        Assert.Single(groups);
        Assert.Equal(new[] { "a", "c" }, g.AppIds);
        Assert.Equal(new[] { "b" }, freed);
    }

    [Fact]
    public void CreatingWithAlreadyGroupedAppsMovesThem()
    {
        var groups = new List<AppGroup>();
        var old = AppGroups.Create(groups, "a", "b");
        old.AppIds.Add("c");
        var g = AppGroups.Create(groups, "a", "x");
        Assert.Equal(new[] { "b", "c" }, old.AppIds);
        Assert.Equal(new[] { "a", "x" }, g.AppIds);
    }

    [Fact]
    public void GroupedAppIdsCoversEveryMember()
    {
        var groups = new List<AppGroup>();
        AppGroups.Create(groups, "a", "b");
        AppGroups.Create(groups, "c", "d");
        Assert.Equal(new[] { "a", "b", "c", "d" }, AppGroups.GroupedAppIds(groups).OrderBy(x => x));
    }

    [Fact]
    public void OrderAfterGroupingPutsTheGroupWhereTheTargetWasAndDropsTheDraggedItem()
    {
        var order = AppGroups.OrderAfterGrouping(new[] { "a", "b", "c", "d" }, draggedId: "a", targetId: "c", resultingGroupId: "gNEW");
        Assert.Equal(new[] { "b", "gNEW", "d" }, order);
    }

    [Fact]
    public void DroppingOntoAnExistingGroupKeepsItsId()
    {
        var order = AppGroups.OrderAfterGrouping(new[] { "a", "g1", "c" }, draggedId: "c", targetId: "g1", resultingGroupId: "g1");
        Assert.Equal(new[] { "a", "g1" }, order);
    }

    [Fact]
    public void DissolveRemovesJustThatGroup()
    {
        var groups = new List<AppGroup>();
        var g1 = AppGroups.Create(groups, "a", "b");
        var g2 = AppGroups.Create(groups, "c", "d");
        AppGroups.Dissolve(groups, g1.Id);
        Assert.Equal(new[] { g2 }, groups);
    }
}
