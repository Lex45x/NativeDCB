namespace NativeDCB.Model;

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