namespace PowerLedger.Contracts;

public enum SessionReason
{
    Boot,
    ServiceStart,
    Resume,
    Suspend,
    Shutdown,
    ServiceStop,
    CrashRecovered,
}
