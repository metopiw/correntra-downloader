namespace Correntra.Transfer;

public class TransferException : Exception
{
    public TransferException(string message)
        : base(message)
    {
    }

    public TransferException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class InvalidRangeResponseException : TransferException
{
    public InvalidRangeResponseException(string message)
        : base(message)
    {
    }
}

public sealed class RemoteResourceChangedException : TransferException
{
    public RemoteResourceChangedException(string message)
        : base(message)
    {
    }
}

public sealed class HashMismatchException : TransferException
{
    public HashMismatchException(string expected, string actual)
        : base($"The downloaded file hash did not match. Expected {expected}, received {actual}.")
    {
        Expected = expected;
        Actual = actual;
    }

    public string Expected { get; }

    public string Actual { get; }
}
