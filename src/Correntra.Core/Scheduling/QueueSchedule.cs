using Correntra.Core.Internal;

namespace Correntra.Core.Scheduling;

[Flags]
public enum ScheduleDays
{
    None = 0,
    Monday = 1 << 0,
    Tuesday = 1 << 1,
    Wednesday = 1 << 2,
    Thursday = 1 << 3,
    Friday = 1 << 4,
    Saturday = 1 << 5,
    Sunday = 1 << 6,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    Weekend = Saturday | Sunday,
    EveryDay = Weekdays | Weekend,
}

public sealed class QueueSchedule
{
    private const ScheduleDays AllDefinedDays = ScheduleDays.EveryDay;

    public QueueSchedule(
        ScheduleDays days,
        TimeOnly startTime,
        TimeOnly? stopTime,
        string timeZoneId,
        bool isEnabled = true)
    {
        if (days == ScheduleDays.None || (days & ~AllDefinedDays) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(days));
        }

        if (stopTime == startTime)
        {
            throw new ArgumentException("Start and stop times cannot be identical.", nameof(stopTime));
        }

        Days = days;
        StartTime = startTime;
        StopTime = stopTime;
        TimeZoneId = Guard.NotNullOrWhiteSpace(timeZoneId, nameof(timeZoneId), 200);
        IsEnabled = isEnabled;
    }

    public ScheduleDays Days { get; }

    public TimeOnly StartTime { get; }

    public TimeOnly? StopTime { get; }

    public string TimeZoneId { get; }

    public bool IsEnabled { get; }

    public bool IsActiveAt(DateTimeOffset instantUtc, TimeZoneInfo timeZone)
    {
        Guard.UtcTimestamp(instantUtc, nameof(instantUtc));
        ArgumentNullException.ThrowIfNull(timeZone);
        if (!IsEnabled)
        {
            return false;
        }

        DateTimeOffset local = TimeZoneInfo.ConvertTime(instantUtc, timeZone);
        TimeOnly localTime = TimeOnly.FromDateTime(local.DateTime);
        ScheduleDays localDay = FromDayOfWeek(local.DayOfWeek);

        if (StopTime is null)
        {
            return Days.HasFlag(localDay) && localTime >= StartTime;
        }

        if (StartTime < StopTime.Value)
        {
            return Days.HasFlag(localDay) && localTime >= StartTime && localTime < StopTime.Value;
        }

        if (localTime >= StartTime)
        {
            return Days.HasFlag(localDay);
        }

        if (localTime < StopTime.Value)
        {
            ScheduleDays previousDay = FromDayOfWeek(local.AddDays(-1).DayOfWeek);
            return Days.HasFlag(previousDay);
        }

        return false;
    }

    public bool IsActiveAt(DateTimeOffset instantUtc)
    {
        TimeZoneInfo timeZone = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        return IsActiveAt(instantUtc, timeZone);
    }

    public static ScheduleDays FromDayOfWeek(DayOfWeek day)
    {
        return day switch
        {
            DayOfWeek.Monday => ScheduleDays.Monday,
            DayOfWeek.Tuesday => ScheduleDays.Tuesday,
            DayOfWeek.Wednesday => ScheduleDays.Wednesday,
            DayOfWeek.Thursday => ScheduleDays.Thursday,
            DayOfWeek.Friday => ScheduleDays.Friday,
            DayOfWeek.Saturday => ScheduleDays.Saturday,
            DayOfWeek.Sunday => ScheduleDays.Sunday,
            _ => throw new ArgumentOutOfRangeException(nameof(day)),
        };
    }
}
