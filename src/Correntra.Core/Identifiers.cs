using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Correntra.Core;

public readonly record struct JobId
{
    public JobId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A job ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public bool IsEmpty => Value == Guid.Empty;

    public static JobId Create() => new(Guid.NewGuid());

    public static JobId Parse(string value) => new(Guid.ParseExact(value, "N"));

    public static bool TryParse([NotNullWhen(true)] string? value, out JobId result)
    {
        if (Guid.TryParseExact(value, "N", out Guid parsed) && parsed != Guid.Empty)
        {
            result = new JobId(parsed);
            return true;
        }

        result = default;
        return false;
    }

    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}

public readonly record struct CategoryId
{
    public CategoryId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A category ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public bool IsEmpty => Value == Guid.Empty;

    public static CategoryId Create() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}

public readonly record struct CategoryRuleId
{
    public CategoryRuleId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A category rule ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static CategoryRuleId Create() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}

public readonly record struct QueueId
{
    public QueueId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A queue ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public bool IsEmpty => Value == Guid.Empty;

    public static QueueId Create() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}

public readonly record struct IpcRequestId
{
    public IpcRequestId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An IPC request ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public bool IsEmpty => Value == Guid.Empty;

    public static IpcRequestId Create() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}
