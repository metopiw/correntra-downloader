namespace Correntra.Transfer;

public static class SegmentPlanner
{
    public static IReadOnlyList<ByteRange> Plan(long contentLength, int maximumSegments, long minimumSegmentSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(contentLength);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumSegments, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumSegmentSize, 1);

        if (contentLength == 0)
        {
            return Array.Empty<ByteRange>();
        }

        var segmentsBySize = ((contentLength - 1) / minimumSegmentSize) + 1;
        var count = (int)Math.Min(maximumSegments, Math.Max(1, segmentsBySize));
        var baseLength = contentLength / count;
        var remainder = contentLength % count;
        var result = new ByteRange[count];
        long cursor = 0;

        for (var index = 0; index < count; index++)
        {
            var length = baseLength + (index < remainder ? 1 : 0);
            result[index] = new ByteRange(cursor, checked(cursor + length - 1));
            cursor += length;
        }

        return result;
    }
}
