using System.Globalization;

namespace BeijingTimeOverlay;

internal static class HoverInput
{
    private static readonly int[] MouseKeys = { 1, 2, 4, 5, 6 };

    public static bool AnyMouseButtonDown(Func<int, short> readAsyncKeyState)
    {
        foreach (var key in MouseKeys)
        {
            // Only the high bit means currently held. The low (historical)
            // bit is unreliable and must not suppress background hovering.
            if ((readAsyncKeyState(key) & 0x8000) != 0) return true;
        }
        return false;
    }
}

// Pure decision about whether the pointer can reach the clock. pointerTarget is
// the top-level window Windows itself would deliver a click to at the cursor
// (zero when nothing hit-testable is there). Hit testing already skips hidden,
// cloaked, click-through and fully transparent layered windows, which a z-order
// rectangle scan wrongly counts as covering the clock.
internal static class HoverOcclusion
{
    public static bool IsExposed(IntPtr pointerTarget, IntPtr clock, IntPtr card, Func<IntPtr, bool> isAboveClock)
    {
        if (pointerTarget == IntPtr.Zero || pointerTarget == clock) return true;
        if (card != IntPtr.Zero && pointerTarget == card) return true;
        // Another window is under the pointer. It only covers the clock when it
        // sits above it; a window below a click-through clock does not.
        return !isAboveClock(pointerTarget);
    }
}

// No window or wall-clock dependency: hover timing uses a monotonic timestamp.
internal sealed class HoverPreviewState
{
    private long? _enteredAt;

    public bool Update(bool eligible, long now)
    {
        if (!eligible)
        {
            Reset();
            return false;
        }

        _enteredAt ??= now;
        return now - _enteredAt.Value >= 300;
    }

    public void Reset() => _enteredAt = null;
}

// Writes one diagnostic line when the pointer rests on the clock but the preview
// still does not appear, so an intermittent failure leaves evidence of which
// condition blocked it. Rate limited; the log file is capped.
internal sealed class HoverTrace
{
    internal const long StuckAfterMs = 2000;
    internal const long RepeatAfterMs = 60_000;
    private const long MaxLogBytes = 64 * 1024;

    private readonly Action<string> _write;
    private long? _since;
    private long? _lastWritten;

    public HoverTrace(Action<string> write) => _write = write;

    public static string DefaultLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BeijingTimeOverlay",
        "hover-trace.log");

    public void Observe(bool shouldShow, bool shown, long now, Func<string> describe)
    {
        if (!shouldShow || shown)
        {
            _since = null;
            return;
        }

        _since ??= now;
        if (now - _since.Value < StuckAfterMs) return;
        if (_lastWritten is long last && now - last < RepeatAfterMs) return;
        _lastWritten = now;
        _write(describe());
    }

    public static void WriteToDefaultLog(string line)
    {
        try
        {
            var path = DefaultLogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length > MaxLogBytes)
            {
                File.Delete(path);
            }

            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch (IOException)
        {
            // Diagnostics must never affect the clock.
        }
        catch (UnauthorizedAccessException)
        {
            // Diagnostics must never affect the clock.
        }
    }
}

internal readonly record struct PacificTimeText(string Time, string Date, string Zone)
{
    public static PacificTimeText FromUtc(DateTimeOffset utc, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(utc, zone);
        return new PacificTimeText(local.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            local.ToString("yyyy/M/d", CultureInfo.InvariantCulture),
            zone.IsDaylightSavingTime(local) ? "PDT" : "PST");
    }
}
