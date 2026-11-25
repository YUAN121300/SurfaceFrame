namespace SurfaceFrame;

public static class FrameTiming
{
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan PongTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan SessionDeadline = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan AwayBeforeSleep = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan HaIdleReturn = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan SlideshowDefault = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaintenanceWake = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan QuickConfirm = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan SlowConfirm = TimeSpan.FromSeconds(6);
}

public static class ReconnectBackoff
{
    public const int InitialMs = 1000;
    public const int MaxMs = 15000;

    public static int Next(int currentMs)
    {
        if (currentMs < InitialMs)
            currentMs = InitialMs;
        var next = (long)currentMs * 2;
        return next > MaxMs ? MaxMs : (int)next;
    }
}
