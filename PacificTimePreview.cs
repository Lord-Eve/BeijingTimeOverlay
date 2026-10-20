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

    public static HoverPointerState SamplePointer(Point? cursor, Func<bool> anyButtonDown,
        Func<Point, bool> contains, Func<Point, bool> isExposed)
    {
        // Preserve the original short-circuit order and sample each input once.
        // Null means a later check was not reached, rather than that it failed.
        if (cursor is not Point point) return new(cursor, null, null, null);
        if (anyButtonDown()) return new(cursor, false, null, null);
        if (!contains(point)) return new(cursor, true, false, null);
        return new(cursor, true, true, isExposed(point));
    }
}

internal readonly record struct HoverPointerState(Point? Cursor, bool? MouseButtonsReleased, bool? InsideBounds, bool? Exposed)
{
    public bool Eligible => Cursor.HasValue && MouseButtonsReleased == true && InsideBounds == true && Exposed == true;
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

internal readonly record struct PacificTimeText(string Time, string Date, string Zone, string Weekday, string BeijingWeekday)
{
    private static readonly CultureInfo WeekdayCulture = CultureInfo.GetCultureInfo("zh-CN");

    public string WeekdayComparison => $"{Weekday}（北京时间：{BeijingWeekday}）";

    public static PacificTimeText FromUtc(DateTimeOffset utc, TimeZoneInfo zone, TimeZoneInfo beijingZone)
    {
        var local = TimeZoneInfo.ConvertTime(utc, zone);
        var beijing = TimeZoneInfo.ConvertTime(utc, beijingZone);
        return new PacificTimeText(local.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            local.ToString("yyyy/M/d", CultureInfo.InvariantCulture),
            zone.IsDaylightSavingTime(local) ? "PDT" : "PST",
            local.ToString("dddd", WeekdayCulture), beijing.ToString("dddd", WeekdayCulture));
    }
}
