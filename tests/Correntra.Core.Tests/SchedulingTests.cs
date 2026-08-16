using Correntra.Core.Scheduling;

namespace Correntra.Core.Tests;

public sealed class SchedulingTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void SameDayWindowIncludesStartAndExcludesStop()
    {
        QueueSchedule schedule = new(
            ScheduleDays.Monday,
            new TimeOnly(9, 0),
            new TimeOnly(17, 0),
            Utc.Id);

        Assert.True(schedule.IsActiveAt(UtcInstant(DayOfWeek.Monday, 9, 0), Utc));
        Assert.True(schedule.IsActiveAt(UtcInstant(DayOfWeek.Monday, 16, 59), Utc));
        Assert.False(schedule.IsActiveAt(UtcInstant(DayOfWeek.Monday, 17, 0), Utc));
        Assert.False(schedule.IsActiveAt(UtcInstant(DayOfWeek.Tuesday, 10, 0), Utc));
    }

    [Fact]
    public void OvernightWindowUsesStartingDay()
    {
        QueueSchedule schedule = new(
            ScheduleDays.Friday,
            new TimeOnly(22, 0),
            new TimeOnly(2, 0),
            Utc.Id);

        Assert.True(schedule.IsActiveAt(UtcInstant(DayOfWeek.Friday, 23, 0), Utc));
        Assert.True(schedule.IsActiveAt(UtcInstant(DayOfWeek.Saturday, 1, 0), Utc));
        Assert.False(schedule.IsActiveAt(UtcInstant(DayOfWeek.Saturday, 3, 0), Utc));
    }

    [Fact]
    public void DisabledScheduleIsNeverActive()
    {
        QueueSchedule schedule = new(
            ScheduleDays.EveryDay,
            TimeOnly.MinValue,
            null,
            Utc.Id,
            isEnabled: false);

        Assert.False(schedule.IsActiveAt(TestData.Timestamp, Utc));
    }

    [Fact]
    public void QueueOperationsReturnNewOrderedAggregate()
    {
        JobId first = JobId.Create();
        JobId second = JobId.Create();
        DownloadQueue empty = new(QueueId.Create(), "Night", 2);

        DownloadQueue populated = empty.Enqueue(first).Enqueue(second).Move(second, 0);
        DownloadQueue removed = populated.Remove(first);

        Assert.Empty(empty.JobIds);
        Assert.Equal([second, first], populated.JobIds.ToArray());
        Assert.Equal([second], removed.JobIds.ToArray());
        Assert.Same(populated, populated.Enqueue(first));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    public void QueueRejectsInvalidConcurrency(int concurrency)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DownloadQueue(QueueId.Create(), "Queue", concurrency));
    }

    private static DateTimeOffset UtcInstant(DayOfWeek day, int hour, int minute)
    {
        DateTime date = new(2026, 8, 10);
        int offset = ((int)day - (int)date.DayOfWeek + 7) % 7;
        return new DateTimeOffset(date.AddDays(offset).AddHours(hour).AddMinutes(minute), TimeSpan.Zero);
    }
}
