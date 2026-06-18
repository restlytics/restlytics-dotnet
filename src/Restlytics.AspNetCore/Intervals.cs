using System.Collections.Generic;

namespace Restlytics.AspNetCore;

/// <summary>
/// Interval-union (sweep-line) helper used to compute per-category "self time".
///
/// Why union and not a plain sum: child spans can overlap (parallel HTTP calls,
/// async queries, nested instrumentation). Summing their durations double-counts
/// the wall-clock time. The union of intervals gives the real wall-clock time
/// actually spent inside that category, which is what the dashboard breakdown and
/// the ingestion service's self-time rollups expect.
///
/// We work in plain 64-bit integer nanoseconds; durations within a single request
/// comfortably fit in an Int64.
/// </summary>
internal static class Intervals
{
    /// <summary>An absolute [start, end] window in nanoseconds.</summary>
    public readonly record struct Interval(long Start, long End);

    /// <summary>Total wall-clock length covered by the union of [start, end] intervals.</summary>
    public static long UnionLength(List<Interval> intervals)
    {
        int count = intervals.Count;
        if (count == 0)
        {
            return 0;
        }

        // Sort by start so a single forward sweep can merge overlaps.
        // Copy first so we don't mutate the caller's list.
        var sorted = new List<Interval>(intervals);
        sorted.Sort(static (a, b) => a.Start.CompareTo(b.Start));

        long total = 0;
        long curStart = sorted[0].Start;
        long curEnd = sorted[0].End;

        for (int i = 1; i < count; i++)
        {
            long s = sorted[i].Start;
            long e = sorted[i].End;
            if (s > curEnd)
            {
                // Disjoint: bank the current run and start a new one.
                total += curEnd - curStart;
                curStart = s;
                curEnd = e;
            }
            else if (e > curEnd)
            {
                // Overlapping: extend the current run.
                curEnd = e;
            }
        }

        total += curEnd - curStart;

        return total;
    }
}
