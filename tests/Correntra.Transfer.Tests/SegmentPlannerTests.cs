namespace Correntra.Transfer.Tests;

public sealed class SegmentPlannerTests
{
    [Fact]
    public void Plan_CreatesContiguousBalancedRanges()
    {
        var ranges = SegmentPlanner.Plan(1_003, 4, 100);

        Assert.Equal(4, ranges.Count);
        Assert.Equal(0, ranges[0].Start);
        Assert.Equal(1_002, ranges[^1].EndInclusive);
        Assert.Equal(1_003, ranges.Sum(range => range.Length));
        Assert.All(ranges.Zip(ranges.Skip(1)), pair =>
            Assert.Equal(pair.First.EndInclusive + 1, pair.Second.Start));
        Assert.True(ranges.Max(range => range.Length) - ranges.Min(range => range.Length) <= 1);
    }

    [Fact]
    public void Plan_EmptyResourceHasNoRanges()
    {
        Assert.Empty(SegmentPlanner.Plan(0, 8, 1024));
    }
}
