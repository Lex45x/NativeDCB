namespace NativeDCB.Engine.Storage.EventLog;

public sealed class EventStoreUnavailableException : IOException
{
    public EventStoreUnavailableException(string message)
        : base(message)
    {
    }

    public EventStoreUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}