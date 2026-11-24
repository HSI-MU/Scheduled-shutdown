namespace OnTimeShutdown.Services;

/// <summary>
/// Produces a coarse marker for the current Windows boot. A stored shutdown
/// plan whose marker no longer matches was lost when the PC restarted.
/// </summary>
public static class BootClock
{
    public static long CurrentMarker()
    {
        long milliseconds = Environment.TickCount64;
        long bootTicks = DateTime.UtcNow.Ticks - milliseconds * TimeSpan.TicksPerMillisecond;
        return bootTicks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute;
    }

    public static bool IsFromCurrentBoot(long marker)
    {
        if (marker <= 0)
        {
            return true;
        }

        return Math.Abs(CurrentMarker() - marker) < TimeSpan.TicksPerMinute * 3;
    }
}
