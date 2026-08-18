namespace NativeDCB.Model.Databases;

public enum DatabaseStatus
{
    Discovered,
    AcquiringLock,
    Recovering,
    Ready,
    Draining,
    Stopped,
    Faulted
}