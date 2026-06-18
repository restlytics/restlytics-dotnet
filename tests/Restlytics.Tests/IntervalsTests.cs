using System.Collections.Generic;
using Restlytics.AspNetCore;
using Xunit;

namespace Restlytics.Tests;

public class IntervalsTests
{
    private static long Union(params (long Start, long End)[] pairs)
    {
        var list = new List<Intervals.Interval>(pairs.Length);
        foreach ((long start, long end) in pairs)
        {
            list.Add(new Intervals.Interval(start, end));
        }

        return Intervals.UnionLength(list);
    }

    [Fact]
    public void EmptyIsZero()
    {
        Assert.Equal(0, Intervals.UnionLength(new List<Intervals.Interval>()));
    }

    [Fact]
    public void SingleInterval()
    {
        Assert.Equal(10, Union((0, 10)));
    }

    [Fact]
    public void DisjointIntervalsSum()
    {
        // [0,10] + [20,25] = 10 + 5
        Assert.Equal(15, Union((0, 10), (20, 25)));
    }

    [Fact]
    public void OverlappingIntervalsAreUnionedNotSummed()
    {
        // [0,10] and [5,15] overlap → union is [0,15] = 15 (NOT 10+10=20).
        Assert.Equal(15, Union((0, 10), (5, 15)));
    }

    [Fact]
    public void FullyContainedInterval()
    {
        // [2,4] inside [0,10] → just 10.
        Assert.Equal(10, Union((0, 10), (2, 4)));
    }

    [Fact]
    public void AdjacentTouchingIntervalsMerge()
    {
        // [0,10] and [10,20] touch at 10 → continuous [0,20] = 20.
        Assert.Equal(20, Union((0, 10), (10, 20)));
    }

    [Fact]
    public void UnsortedInputIsHandled()
    {
        Assert.Equal(15, Union((20, 25), (0, 10)));
    }

    [Fact]
    public void MultipleOverlapsChained()
    {
        // [0,5],[3,8],[7,12] all chain → [0,12] = 12.
        Assert.Equal(12, Union((0, 5), (3, 8), (7, 12)));
    }

    [Fact]
    public void ZeroLengthIntervals()
    {
        // Cache markers are zero-length; they contribute nothing on their own.
        Assert.Equal(0, Union((5, 5), (10, 10)));
    }
}
